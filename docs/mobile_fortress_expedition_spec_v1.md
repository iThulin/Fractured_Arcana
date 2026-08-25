# The Mobile Fortress — Expedition Reframe Spec v1.1

**Status:** approved for build, 2026-08-21. All §14 rulings resolved by Magos
(v1.1): crew stations DORMANT in ambush defense; ambush rates kept and tuned in
playtest; one placeholder castle model; rest-site refuel APPROVED. v1.1 adds
§3.4 Stride Orders (click-to-navigate with commitment bonus + casting lockout).
Founding rulings (v1): castle damaged-never-lost; fuel with field refueling;
castle types school-bound; slots hold new castle modules.
**Supersedes the fiction of:** party-of-soldiers expeditions. **Preserves the
mechanics of:** the sliding window, POIs/encounters, the lunation cadence, the
extraction economy, combat round-trips, and the run log.

The player no longer walks a party across the world. They sit in the campus
scrying chamber and command a **mobile fortress** — a walking castle in the
Howl's tradition — crewed by their companions, burning fuel as it strides the
map. The wizard is never "out there": they are at home, directing through the
lens, teleporting into fights by waystone, and being interrupted by messengers.
The 3D expedition view (the scrying-table rig, the mist, the disc, the chamber)
already IS this fiction's rendering — this spec makes the mechanics agree with
what the screen has been showing since the scrying rig landed.

---

## 0. Counterarguments, stated first

1. **This is a re-skin wearing a redesign's clothes.** Most of the brief is
   achievable as fiction over existing systems (steps→fuel is a rename; the
   wizard already "was" in the chamber). The genuinely new mechanics are: the
   ambush castle-defense combat, crew-derived castle stats, field modules, and
   per-school movement signatures. The spec keeps the re-skin parts as
   re-skins (cheap, zero rebalance) and spends complexity only on the four new
   mechanics. *Do not let the fiction pass tempt a rewrite of systems that
   already work.*
2. **Castle defense adds a second defense combat.** Protect/ward objectives
   (`CombatObjectiveDef.Kind == "ward"`, o3) and the city gate-defense track
   already exist. If castle defense becomes a third bespoke system, that's a
   parallelization failure. *Mitigation:* castle defense IS a ward-objective
   combat with the Castle Heart as the ward unit and a delayed player spawn —
   no new combat mode.
3. **Crew-as-stats risks double-spending companions.** Companions already carry
   combat kits, arcs, loyalty (K4), and fitness (K5). Making them also castle
   stats could make one hire dominate every subsystem. *Mitigation:* crew
   effects draw ONLY from data that already exists (archetype + loyalty tier),
   are small (±10–15% class effects), and never stack more than one crew
   member per station.
4. **School-bound castles remove a choice.** Ruled by Magos: the castle is the
   school's expression, not a second identity pick. The cost is that castle
   variety is only as replayable as school variety. Accepted; module slots are
   the within-school variance.
5. **Diegetic alerts (scrolls, messengers) can bury information.** A toast is
   glanceable; a messenger walking to a table is not. *Mitigation:* phase P
   (presentation) keeps every existing toast/log ALSO firing until the
   diegetic channel proves legible in playtest. Fiction never deletes
   information.

---

## 1. Identity

**One sentence:** the expedition is a sortie of the guild's walking castle,
commanded remotely from the campus scrying chamber; fuel is the leash, the
crew is the engine room, and the wizard's body only leaves home through a
waystone.

Design rules honoured (house discipline):
- **Extend, don't parallelize.** Fuel wraps the existing step-cost machinery
  (`OverworldMovementCost`), castle defense wraps the ward objective, crew
  stats route through `BuildingEffectApplier.RunBonuses`-style aggregation,
  modules are `Data/`-driven JSON like buildings.
- **Additive save fields, no version bump.** Every new field is additive with
  lazy backfill; serialized names that already exist (`SavedStepsRemaining`
  etc.) are kept as-is and documented as fuel-in-disguise (renaming serialized
  fields is not worth a migration).
- **Starting values are starting values.** Every number below is a tuning
  seed, not a commitment.

---

## 2. The Castle

### 2.1 Hull (replaces party expedition HP as the sortie's health)
- `Hull / MaxHull` is the castle's structural pool. Terrain hazards that drained
  party HP (swamp miasma, corruption ground) now drain Hull — same numbers,
  same `TerrainHPDrain`/`CorruptionDrainAt` code paths, relabeled.
- Companions keep their individual K2.5 carried-HP for combats; the castle
  absorbs the overworld attrition instead of the party pool.
- **Hull 0 = forced recall** (ruling: damaged, never lost). Identical
  consequences to today's emergency extraction: the castle limps home, +1
  lunation straggle, §5b injury rolls for crew, spoils kept. The eternal-campus
  pillar extends to the castle: it is never destroyed, never rebuilt, never
  lost. Repairs are part of the between-sortie turnaround (below).

### 2.2 Recall = extraction (fiction upgrade, mechanics preserved)
- **Free recall** on a supply anchor (staging, secured outpost, standing
  waystation) — unchanged from W3.
- **Emergency recall** anywhere — unchanged costs (straggle lunation, injury
  band). The confirm dialog copy becomes the recall order.
- **Turnaround lore** (the answer to "why one sortie per lunation"): recalling
  the castle means refueling, restocking the holds, unloading cargo, and hull
  repairs. This is narration for the EXISTING lunation cadence — no new timer.
  The Herald's Report gains one flavor line summarizing the turnaround.

### 2.3 What walks on the map
- The party token/pawn becomes the castle model (per-school silhouette; the
  cone pawn is the placeholder). One tile occupancy, same picking, same
  movement rules — the castle is big in fiction, one hex in mechanics.

---

## 3. Fuel (replaces steps; ruling: fuel + field refueling)

### 3.1 Burn
- `Fuel / MaxFuel` replaces `StepsRemaining / OperatingRange`. **Burn per tile
  = the existing `OverworldMovementCost.StepCost`** (terrain table, road
  discount, ford penalty, pathfinder gear, traversal spells) — the entire
  route-planning economy carries over untouched. UI label: "Fuel", gauge
  rendered as the castle's furnace dial.
- MaxFuel starting value = 40 (current OperatingRange), modified by castle
  type (§4), crew (§5), and modules (§6).

### 3.2 Field refueling (the new verb)
- **Secured outposts and seats refuel fully** (they already grant full-heal
  rest; add fuel to the same arrival handler).
- **Supply caches refuel +8** on collection (additive field on the cache
  payout; the cache fiction is already "supplies").
- **Rest sites (refuges) refuel +5.** Rest currently restores HP/Essence but
  no steps; this is the one deliberate economy change — small, because
  refuges are common. *Watch in playtest: if routes stop feeling finite, cut
  to +3 or 0.*
- **Fuel never exceeds MaxFuel; no fuel → the castle is stranded**: only
  recall (free on anchor, emergency anywhere) remains. Identical to steps-0
  today.

### 3.3 Explicit non-goals (ruled out for v1)
No fuel-as-cargo weight, no fuel types, no overloading (the "full logistics"
option was declined). Revisit only if playtest says route planning got too
soft.

### 3.4 Stride orders (v1.1 — click-to-navigate with commitment)

The player can command destinations, not just steps: click a distant tile and
the castle **strides** there along a computed path, one simulated step at a
time. Fits the fiction exactly — a commander at a scrying table gives orders,
not joystick inputs.

**Pathfinding.**
- A* over the fuel-cost function (`OverworldMovementCost.StepCost` as edge
  weight — the planner and the charge can never disagree), water impassable.
- **Plan only across scried ground:** tiles at fog Revealed (and Silhouette,
  at pessimistic default cost 2) are traversable for planning; Hidden tiles
  are not, and a Hidden destination is not orderable. The lens cannot command
  what it cannot see — fog keeps its teeth.
- Known POI tiles carry a **path weight penalty** (+6, starting value) so a
  stride routes around them rather than face-planting into encounters; a POI
  as the *destination* is ordered normally.

**Execution — the world simulates every step.**
Each stride step is a full ordinary move: fuel burn, Hull drain, vision
update, patrol/roamer movement, ambush interception checks. A stride HALTS
immediately on: arrival; any encounter/combat/ambush trigger; entering a tile
whose reveal shows a POI on the remaining path (re-plan prompt); fuel
insufficient for the next step; Hull reaching 25% MaxHull (safety halt); or
player cancel (the one order accepted mid-stride). Step pacing ~0.25s per
tile so the march is watchable and cancelable.

**Momentum (the commitment bonus).**
From the **4th consecutive step** of an uninterrupted stride onward, each
step's burn is reduced by 1 (floor 1) — the castle finds its gait. Momentum
resets on any halt. Stacking rule: momentum applies AFTER type/crew modifiers
but shares the same floor; with the Chronomancer's first-3-flat quirk, take
the cheaper of the two per step, never both.

**The cost — the Grimoire seals while striding.**
No overworld spells may be cast from order confirmation until the castle
halts (the wizard's hands are on the helm, not the stave). The Grimoire panel
greys with "the castle must halt to channel." Passive/armed charges (e.g.
Campward) persist; only *casting* locks. **Ambush while striding: the wizard's
teleport delay is +1 round** (unbraced — starting value, cut if defense
winrates crater; Wardroom/Waystone Focus still subtract).

**UI.** Hover shows the path preview ribbon + total fuel estimate (with
momentum savings) before the confirming click; while striding, a "Halt"
button replaces the spell shortcut row. Preview and charge share one code
path (G1 discipline: the preview must never lie).

---

## 4. Castle types (school-bound — ruling)

The castle is the school's expression; selecting the school at founding selects
the chassis. Each type = one **movement signature** + one **operating quirk**.
Signatures reuse existing cost/vision/spell primitives only. Starting values.

| School | Castle | Movement signature | Operating quirk |
|---|---|---|---|
| Adept | The Bastion Errant | none (baseline) | +5 MaxFuel (the generalist's deeper tank) |
| Elementalist | The Cinderhold | Volcanic/Desert burn −1 (min 1) | immune to weather Hull drain |
| Druid | The Verdant Ark | Forest/Swamp burn −1 (min 1) | Rest-site refuel doubled |
| Necromancer | The Ossuary Ambulant | Ruins/corrupted burn −1 | corruption Hull drain halved |
| Tinker | The Gearspire | road discount doubled (−2, min 1) | +1 module slot (3 total) |
| Enchanter | The Lantern Keep | ford penalty waived | wards: ambush chance −20% |
| Arcanist | The Orrery Bastille | Hills/Mountain burn −1 | scry radius +1 (vision) |
| Chronomancer | The Hourglass Redoubt | first 3 moves each sortie burn 1 flat | one free re-roll of a district/POI reveal per sortie |

Implementation: a `CastleTypeDef` table (code const or `Data/Castles/*.json` —
see §10) keyed by `CardSchool`; movement hooks read it inside `StepCost`'s
caller (one adjustment site, mirroring `pathfinderReduction`), quirks hook
their existing systems (ambush roll, corruption drain, vision radius).

---

## 5. Crew (companions determine castle characteristics)

The active party IS the crew. Each sortie, crew members auto-assign (player can
reorder) to **stations**; a station takes exactly one crew member and grants a
castle effect derived from data that already exists — archetype and K4 loyalty
tier. No new companion fields.

| Station | Effect (starting values) | Best-in-slot archetype |
|---|---|---|
| Helm | −10% total fuel burn (rounding floor 1) | Survivor |
| Furnace | +5 MaxFuel | Commander |
| Lens Room | +1 scry/vision radius | Scholar |
| Quartermaster | +1 item drop rarity weight shift on combat loot | Merchant |
| Wardroom | ambush wizard delay −1 round (2 → 1) | Idealist |

- Effect strength scales with loyalty: Wary = half effect, Neutral = full,
  Sworn = full +25%. (Reuses the K4 ladder; the fiction: a wary crew member
  works the station badly.)
- A matching archetype gives the listed effect; a mismatched one gives a flat
  weak version (e.g., Helm −5%). This makes crew composition a real loadout
  decision without inventing stats.
- Empty stations (party smaller than stations) simply grant nothing.
- Combat is unchanged: the crew teleports to the fight with the wizard (§7);
  stations are dormant during combat.

---

## 6. Field modules (ruling: new module set)

**Slots: 2** (Tinker: 3). Installed between sorties at the campus (no in-field
swapping v1); costs paid in materials/gold. New content type
`Data/CastleModules/*.json` — deliberately small: id, name, cost, one effect
key + magnitude, flavor line. Launch set (8):

| Module | Effect |
|---|---|
| Auxiliary Furnace | +8 MaxFuel |
| Ley Siphon | refuel +2 whenever the castle ends a move on ArcaneGround |
| Farseeing Array | +1 scry radius |
| Waystone Focus | ambush wizard delay −1 round (stacks with Wardroom to min 0) |
| Reinforced Keel | +25% MaxHull |
| Cargo Sling | +1 guaranteed combat loot roll per sortie (first Siege/Boss win) |
| Herald's Perch | one free Courier-style chart (3-hex radius) at sortie start |
| Storm Anchors | weather/hazard Hull drain −50% (overlaps Cinderhold — reroute per school later) |

Effect keys aggregate through the same pass as building run-bonuses (extend
`BuildingEffectApplier.CalculateRunBonuses` output struct — no new manager).

---

## 7. Combat

### 7.1 Non-ambush encounters — the Waystone (fiction only, zero rebalance)
The wizard + crew teleport into the fight via waystone. Mechanically this is
**exactly today's combat**: same roster, same spawn, same first turn. The
change is presentation: launch transition shows the waystone flash (later
pass), and combat intro copy says the wizard steps through. *Deliberately no
waystone-charge resource in v1 — the brief doesn't require one and it would
tax every fight.*

### 7.2 Ambush — Defend the Castle (the one real combat change)
When the roamer/patrol catches the castle (`SavedCombatWasPatrolAmbush` path):
- Map: the castle interior/courtyard recipe (reuse a battlefield map, or the
  `city_streets` recipe when the siege track ships — coordinate with brother,
  do NOT build a third defense map system).
- Objective: **ward** (`CombatObjectiveDef.Kind = "ward"`, existing) with the
  **Castle Heart** as the ward unit (stationary structure unit, HP scaled to
  MaxHull share). Heart destroyed = combat loss → forced recall consequences.
- **The wizard is NOT present at start.** Crew deploys alone. The wizard
  teleports in at the start of **round 3** (delay 2; Wardroom/Waystone Focus
  reduce it, floor 0 = present from round 1) at the Heart's position, with a
  one-round "translocation shock" (wizard acts with hand drawn but no
  channeled/ultimate the arrival round — exact restriction tuned in combat
  code review).
- Victory: normal rewards + the ambush purse. The delay mechanic is the
  ambush's teeth now; consider softening the ambush enemy count later if
  winrates crater (starting stance: no change).

### 7.3 Hull in combat
Combat damage does not touch Hull (combat has its own HP economy); the Heart
in ambush defense is the exception by proxy. Overworld hazards are the only
Hull drains. Keeps the two damage economies cleanly separated.

---

## 8. Presentation: commanding from the chamber (phase P — after mechanics)

The scrying-table rig becomes the diegetic UI shell. Phased so mechanics never
wait on art:
- **P1 — Messenger toasts.** Every existing toast ALSO spawns a chamber beat:
  a scroll placed on the table's edge, or a messenger figure entering and
  bowing (reuse the stand-in companion figure rig). The toast text is the
  scroll's content on click. Toasts keep firing as today until P1 proves
  legible (counterargument #5).
- **P2 — Spellcasting body.** Overworld spells show the wizard figure at the
  table casting; the effect animates ON the scrying disc (the S1/S2 spell
  visuals already render there — this adds only the caster beat).
- **P3 — Waystone transitions.** Combat entry/exit framed as the waystone
  flash from the chamber.
- **P4 — Turnaround tableau.** Between sorties, the chamber shows the castle
  docked (small model on the table's side plinth) with refuel/repair/unload
  progress as the lunation's flavor.

## 9. Council concurrency

Because the wizard's body never leaves the campus, **the Council screen is
openable mid-sortie** (today it is reachable only outside expeditions — the
strategic/expedition split). Scope for v1:
- Opening the Council pauses the expedition scene (it is turn-based; pause is
  free) and shows the council chamber as today.
- Envoy/gift/smear actions queue normally; their lunation ticks resolve on the
  next turnaround as they already do. NO new mid-sortie council resolution —
  the calendar only advances between sorties, so this is UI unlock + fiction,
  not a simulation change.
- The representatives-walking-out beat is P-phase presentation on the council
  screen, not a blocker.

---

## 10. Data model & migration

- `GuildSaveData/CycleState` additions (additive, lazy backfill, NO version
  bump): `CastleModulesInstalled : List<string>`, `CrewStations :
  Dictionary<string,string>` (station → companion id; empty = auto-assign),
  `CastleHullDamageCarried : int` (post-sortie repair hook, default 0 =
  full repair each turnaround; reserved for a later hardship lever, unused
  v1).
- **Renames are display-only.** `StepsRemaining`, `OperatingRange`,
  `SavedStepsRemaining` keep their code/serialized names (documented as fuel);
  UI strings say Fuel/Furnace. Rationale: zero save risk, zero router churn.
  A cosmetic rename pass can follow once stable.
- Castle types: `CastleTypeDef` as a code table in v1 (8 entries, small);
  promote to `Data/Castles/*.json` only if modding/iteration demands it.
- Modules: `Data/CastleModules/*.json` + loader mirroring `ItemDatabase`'s
  pattern; effects surface through the run-bonus aggregation.

## 11. Explicitly unchanged

The sliding window, fog/discovery, POI taxonomy and handlers, encounter
assembly/pools, negotiation, the run event log schema (add `fuel` alongside
`steps` columns rather than replacing), combat rewards, splinters/loot,
injury system, the lunation calendar, warfronts, shard zones, and the
Convergence. The reframe must be invisible to all of them.

---

## 12. Build order (each increment compiles + plays before the next)

- **F1 — Fuel skin + refueling.** UI relabel, furnace gauge, outpost/cache/
  rest refuel hooks. *Accept:* sortie plays identically except refuel points
  extend range; run log shows fuel lines.
- **F2 — Hull.** Overworld drains → Hull; Hull-0 recall path; turnaround
  flavor line. *Accept:* swamp crossing damages castle not party; Hull 0
  forces the straggle recall.
- **F3 — Castle types.** School-keyed table, movement signatures + quirks.
  *Accept:* Druid castle crosses forest cheaper than Adept castle, per log.
- **F4 — Crew stations.** Auto-assign + reorder UI (one panel), effects
  live. *Accept:* swapping Helm crew changes measured burn.
- **F5 — Modules.** JSON set, install UI at campus, effect aggregation.
  *Accept:* Auxiliary Furnace shows +8 MaxFuel next sortie.
- **F6 — Ambush castle defense.** Ward-objective combat, Heart unit, delayed
  wizard spawn, delay reducers. *Accept:* ambush plays the defense; wizard
  arrives round 3; Wardroom makes it round 2.
- **F7 — Council unlock.** Mid-sortie council access. *Accept:* open council
  during a sortie, queue a gift, no state corruption on return.
- **F8 — Stride orders** (§3.4; depends only on F1, can run any time after
  it). A* planner over scried ground, step-by-step execution with full
  per-step simulation, halt conditions, momentum discount, Grimoire lock,
  path preview. *Accept:* click a revealed tile 8 hexes off → castle walks
  there stepwise with patrols moving each step; steps 4+ burn −1; casting
  greyed until halt; an ambush mid-stride interrupts and the wizard arrives
  one round later than a standing ambush.
- **P1–P4 — Presentation** (any time after F1, independent).

## 13. Tuning table (starting values, one place)

MaxFuel 40 · outpost refuel full · cache +8 · rest +5 (Verdant Ark +10) ·
Helm −10% burn · Furnace +5 · Wardroom delay −1 · ambush delay 2 rounds ·
translocation shock 1 round · Heart HP = 60% party-HP-pool equivalent ·
module costs 120–300g range · Sworn station bonus +25%, Wary half ·
stride: momentum from step 4 (−1/step, floor 1) · POI path weight +6 ·
Silhouette plan cost 2 · safety halt at 25% Hull · striding-ambush wizard
delay +1 · step pacing 0.25s.

## 14. Rulings — RESOLVED (Magos, 2026-08-21)

1. **Crew stations in ambush defense: DORMANT.** One system per datum;
   station effects are overworld-only and grant nothing inside combat.
2. **Ambush frequency: KEEP current rates**, measure at F6, tune from data.
3. **Castle model: ONE PLACEHOLDER** for all schools; per-school silhouettes
   are P-phase art.
4. **Rest-site refuel (+5): APPROVED.** The §3.2 watch note stands (cut to
   +3/0 if routes stop feeling finite).
