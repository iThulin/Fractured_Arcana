# Session log, 2026-09-21: Expedition v2 step 1, the split-forces data layer

Bound to desktop-2e3anmt, `D:\Development\Fractured Arcana`, on `main` at 88ab403.
Model claude-opus-5. **UNCOMMITTED.** Data and save layer only: no behaviour
changed, nothing rendered, no existing system rewired.

## The rulings this builds to (Magos, 2026-09-21)

1. **Split the forces.** The castle parks and becomes a waypoint. The field
   party travels to KNOWN waypoints: cities, shard zones, built waypoints.
2. **One lunation buys one castle move plus K field dives** (K starts at 3).
3. **Rework the leash** rather than keeping supply anchors as they are.
4. Build order: handoff steps 1 to 4 as written.

## The correction that reshaped step 1

The handoff's "read first" list omits `mobile_fortress_expedition_spec_v1.md`,
which was approved 2026-08-21 and built through F8d on 2026-08-24. Several
things the handoff proposed as new are already shipped:

- The wizard is already the Administrator. Fortress spec section 1: the wizard
  is at home, directing through the lens, teleporting to fights by waystone.
- Castle defense already exists (section 7.2): a ward objective with the Castle
  Heart as the ward unit, crew deploying alone, the wizard arriving round 3.
- Steps are already fuel (`OperatingRange` 40) and party HP is already Hull.
- Castle upgrades already exist as `Data/CastleModules/*.json`, 2 slots, 3 for
  Tinker. That is where waypoint charges should be sourced.
- Abstracted travel partly exists: Stride Orders F8a to F8d, including the
  exploratory march into fog.

The pressure-test document written earlier the same day was wrong on two points
as a result: it claimed no castle crew existed (one does, with CrewStations
F4a) and that no castle upgrade ladder existed (CastleModules is one). Its
supply-leash arithmetic also used `run_structure_v2` figures (25 to 35 steps,
a 45 to 60 party pool) that Fuel and Hull superseded. The shape of that finding
survives; the numbers need redoing against MaxFuel and MaxHull.

## What shipped

Four new files, three surgical additive patches, 84 added lines on tracked
files. No SaveManager version bump: every field is additive with a safe default
and a lazy backfill, per the house rule.

### New

- **`Scripts/Data/SaveState/FieldExpeditionState.cs`** (210 lines).
  `CompanionPosting` (Campus, Crew, Field), `AnchorKind`, `FieldPartyState`,
  `Waypoint`, `FieldParty`, `ExpeditionTurnState`. Enum values are pinned
  explicitly because `SaveManager.JsonOptions` carries no string-enum
  converter, so every enum is int-serialized.
  `ExpeditionTurnState.BeginLunation` is idempotent within a lunation, so a
  reload mid-lunation cannot refund a spent castle move.
  Deliberately absent: a `PartyLoadout` equipment container, because
  `ArmoryData.Loadouts` is already keyed per companion id and equipment
  therefore follows the person into whichever force they join. A party owns a
  roster and consumables, nothing else. Also absent: waypoint affixes.

- **`Scripts/Data/SaveState/SignaturePool.cs`** (215 lines).
  `SignatureSpell`, `SignaturePoolState`, `WizardSignatureSlots`.
  Three tiers, not an alias of the deck: collection (`OwnedCard`) to signature
  pool (bounded, new) to the player's active deck (`ActiveDeckInstanceIds`,
  unchanged). The handoff asked for the deck to simply BE the pool; that
  collides with the shipped schema three ways. Upgrades live per owned copy
  (`OwnedCard.TopTier`), so "upgrading a signature propagates to every wizard"
  has no referent across two copies at different tiers, and needs the
  pool-level record `SignatureSpell` now carries. Aliasing would also cap the
  pool at `MaxDeckSize` 20 and make every card added for an ally bloat the
  player's own draws. And lending was undefined: by blueprint or by instance.
  Rarity points (common 1, uncommon 2, rare 3, legendary 5, budget 8) resolve
  in one place, `WizardSignatureSlots.PointsFor`, so the editor readout and the
  validator cannot disagree.

- **`Scripts/Data/FeatureBuilders/ExpeditionAnchors.cs`** (379 lines).
  The union view answering "where can the field party go?", built over what the
  world already tracks rather than a second destination list beside the 29
  existing `StagingPoints` call sites: staging points (cities and secured
  outposts arrive free, since granting staging is already how the world marks
  them), discovered shard zones, the parked castle, and built waypoints with
  charges left. Also owns `BackfillPostings` (before the split,
  `ActivePartyCompanionIds` WAS the crew), `ReconcilePostings`,
  `EnsurePrimaryParty`, `Crew`, and `TravelPhases`.

- **`Scripts/Systems/Verification/FieldExpeditionSaveAssert.cs`** (504 lines).
  Mirrors `CouncilSaveAssert`: nine assertion groups through the real
  `SaveManager.JsonOptions`, covering every new struct, `CycleState` carrying
  the whole block, and the backfill path, which is the one place a real save
  can silently lose its castle crew.

### Patched (additive only)

- `CycleState.cs`: `CastleX` / `CastleY` (the parked fortress, distinct from
  `LastDeployStagingKey`, which records where a sortie launched rather than
  where the castle now stands), `FieldParties`, `MaxFieldParties`, `Waypoints`,
  `ExpeditionTurn`, `SignaturePool`, `SignatureGrants`.
- `CompanionDefinition.cs`: `Posting` and `FieldPartyId`. These two fields are
  the single source of truth for postings; `FieldParty.MemberCompanionIds` is
  the ordered view that `ReconcilePostings` rebuilds.
- `WorldData.cs` (`WorldPoi`): `Harvestable`, `RegenLunations`,
  `RegenReadyLunation`. The ready lunation is compared against the calendar
  rather than counted down, so quitting mid-cycle cannot lose or gain regrowth.

## Verify (static only, no .NET SDK on the box)

- Em dash check clean on all seven touched files (0 occurrences each).
- Brace, paren and bracket balance zero on all seven.
- Every public expression-bodied property in the new code carries `JsonIgnore`,
  so no derived value is written to the save or silently dropped on read.
- No name collisions: all twelve new types are unique repo-wide, which matters
  because the codebase has no namespaces.
- External symbol wiring checked one to one (`SaveManager.JsonOptions`,
  `WorldData.StagingPoints` / `ShardZones` / `PoiAt` / `HexDistance`,
  `CardDatabase.Blueprints`, `Companion.IsInjured`, `ShardZone.ShardCollected`).
- Logic ported to Python and run against the same scenarios as the C# assert:
  turn budget, backfill, reconcile including the vanished-party and run-twice
  cases, travel phases, and the rarity budget. All passed.
- Pacing arithmetic confirmed for ruling 2: 12 castle moves chart about 1,968
  of 9,216 tiles (21 percent), so a cycle still cannot illuminate the world;
  12 lunations at 3 dives is 36 POIs of 187, in the same band as a radius-12
  sortie's 3 to 6 per deploy.

**In-engine confirm owed:** load a pre-split save, check the old active party
comes back as the castle crew and the bench stays on campus, then run
`FieldExpeditionSaveAssert.AssertAll()` and confirm all nine groups pass.
Godot will generate the `.cs.uid` files for the four new scripts on first
import.

## Open, and deliberately not decided here

- **Waypoint expiry.** The handoff listed `ExpiresAtPhase` / `ExpiresAtLunation`.
  The field exists and defaults to 0, meaning never. A rot timer is the chore
  the redesign exists to remove, so the schema supports expiry and no behaviour
  uses it until ruled.
- **Party size.** `MaxPartySize` is 2 at baseline (`BuildingEffectApplier:108`),
  growing only through Grand Hall tiers, while CrewStations defines five
  stations. Two forces cannot both be staffed from that. Either party-size
  growth funds both rosters, or the crew shrinks to the stations that matter.
  This blocks step 4, not step 1.
- **The leash rework** (ruling 3). Nothing here touches `SupplyRange`,
  `LeashBandWidth` or `LeashDrainPerBand`. Needs its own pass, redone against
  Hull and MaxFuel rather than the retired party-pool numbers.
- **Where charges come from.** `CastleModules` is the natural source, and no
  module grants them yet.

## Increment 2a: the data layer goes live

Deliberately chosen as work that survives either movement model, so nothing is
thrown away if stride stays and strategic-scale castle movement never lands.

- **`ExpeditionManager`**: `RecordCastleWorldPosition` / `RecordCastleLocalPosition`
  helpers plus five call sites. The castle's world position is now written at
  deploy, on every step it takes, and at all three run ends (`Extract`,
  `EmergencyExtract`, `FailExpedition`). `CycleState.CastleX/Y` is therefore
  always correct rather than a field nothing fills.
  **Open ruling recorded at the three run ends:** today every ending narrates
  the castle returning to dock, so that is where it is written. When "the
  castle parks in the field and stays there" is ruled in, those three lines are
  the only thing that changes, because `CastleX/Y` already means the right
  thing everywhere else.
- **`StrategicView._Ready`**: `BackfillPostings` and `ReconcilePostings` run
  beside `SupplyCacheSystem.EnsureSeeded`, which is the existing idempotent
  pre-feature-seed slot and exactly the same role. `ExpeditionTurn.BeginLunation`
  runs after `ProcessPendingStraggle`, so a straggled lunation is already on the
  calendar before the budget rolls.
- **`StrategicDebug.DumpExpeditionState`**: a READ, not a lever. Prints the
  castle position, the lunation budget, the full anchor list as
  `ExpeditionAnchors` builds it, postings by force, every field party, and the
  signature pool. What it prints is exactly what a destination picker would
  offer, so the data layer is observable before anything renders it.
- **`CampusGuildPanel`**: two buttons beside the other strategic levers,
  "Dump Expedition State" and "Assert Field Save".

`ExpeditionAnchors.All` now returns real data: the parked castle plus the
existing staging points and any discovered shard zones. Built waypoints stay
empty until waypoint creation lands, which is the next increment.

Verified: em dash gate clean; brace, paren and bracket balance compared per
file against HEAD and unchanged on all four; every external symbol checked
(`TryLocalToWorld`, `InBounds`, `CurrentLunation`, `IsRecruited`, the local
`Act` helper, `EnsureSeeded`). The `() => FieldExpeditionSaveAssert.AssertAll()`
button follows the shipped `() => CombatDebugLauncher.AssertDeckSplit()`
pattern, which is also a bool-returning method wrapped for an `Action`.

**In-engine confirm owed:** open the campus debug panel, press "Assert Field
Save" (nine groups should pass), then "Dump Expedition State" and check the
castle reads as the staging point, the budget reads lunation N with 0/3 dives,
and the anchor list names Home Camp. Deploy, walk a few tiles, recall, and dump
again: the castle should be back at the dock.

## Increment 2b: parking

Ruled by Magos this session: the castle parks when its fuel runs out, a parked
castle IS a waypoint, and it refuels on the next sortie. Spoils are carried home
by a field party, not banked on parking.

**The trick that made this small.** A parked castle upserts a real
`StagingPoint` with `Source = "Castle"`. The shipped deploy drawer, the
strategic markers and the supply-leash anchor rules all read `StagingPoints`,
so parking earns a launch point, a map marker and a supply anchor without one
line of parallel machinery. `LastDeployStagingKey` is pointed at the new tile,
so the next sortie opens there and the castle carries on from where it stopped
rather than blinking back to the dock. Refuelling then needs no code at all:
deploy already sets `StepsRemaining = OperatingRange`.

Exactly one castle staging point exists at a time, because the castle is in one
place. Parking on a tile that already stages (an outpost the castle stopped on)
leaves that one alone rather than stacking two launch points on one hex.

**Parking is a choice, not a hard stop.** Fuel zero is a soft limit today: you
may press on, grinding Hull per step, and Hull zero forces a recall. That
tension is worth keeping, so "Park Here" appears only once the furnace is dry
and sits alongside it. The hint now names all three options: park and make this
a waypoint, recall and bank, or press on at the cost of Hull.

**What parking deliberately is not.** No straggle lunation, no extraction
infirmary check (the crew has not come home, so `ExpeditionHP` persists into the
next sortie), and no banking. `BankResources` is not called. Stats still count
the sortie; the economy does not, because nothing reached the coffers.

**The hold.** `CastleHold` (gold, splinters, materials, supplies) accumulates
across parks. A recall banks it and unparks. A FAILED run forfeits the sortie's
own spoils per the 2026-08-05 ruling but leaves the hold alone, since the hold
is aboard the castle rather than carried by the run that just ended. That keeps
the loop playable before field party movement exists: nothing can be stranded
permanently.

**Files:** `FieldExpeditionState.cs` (+`CastleHold`), `CycleState.cs`
(+`CastleParked`, `CastleParkedLunation`, `CastleHold`), `ExpeditionAnchors.cs`
(`ParkCastleAt`, `UnparkCastle`, and the castle anchor now gated on
`CastleParked` so a mid-sortie castle is not listed as a launch point),
`ExpeditionManager.cs` (`ParkCastle`, `DockCastle`, the Park button and its
visibility), `StrategicDebug.cs` (parked state and hold in the dump).

Verified: em dash gate clean; balance unchanged against HEAD on every touched
file; `LogRun`, `RunEventLog.End`, `UITheme.ApplyButtonStyle` and the
`GuildSaveData` economy shims all checked against their real signatures.

**In-engine confirm owed:** deploy, spend the fuel down to zero, confirm "Park
Here" appears, press it. The castle should make camp, the run log should read
`parked`, and the dump should show PARKED plus a non-empty hold. Return to the
strategic map: a staging beacon should stand on that tile, and the Gatehouse
deploy should open there with a full tank. Recall from the next sortie and the
hold should unload into your gold.

## Increment 2c: the dry furnace, the repair bill, and exposure

Magos rejected 2b's control surface and expanded the design. New rulings:

1. Running out of fuel FORCES a park.
2. Moving past empty stays possible but must be an explicit decision, paid in Hull.
3. Hull damage lengthens the refuel period, because work crews teleport in to repair.
4. That teleport is the main way the player SEES refuel, restock and treasure coming home.
5. A parked castle mid-resupply is MORE EXPOSED to attack.
6. The point of all of it: stop the player force-marching the castle wherever they like.

**The bug in 2b.** The Park button was placed at `OffsetTop 58..98`. The Ledger
button occupies `60..100` and is added to the canvas afterwards, so Ledger drew
on top and swallowed the clicks. The text fired, the control was unreachable.
Rather than shuffle it to the next free row, the whole approach was wrong: a
button the player has to notice is not how you force a decision.

**The dialog is the control surface now.** At fuel zero the castle halts and a
confirm dialog states the cost both ways. "Make camp" is the default and the
ruled behaviour; "Push on" is the deliberate override. Asked once per sortie,
so a player who chose to push on is not nagged every tile.

**The repair bill.** `1 + floor(4 * missingHull / MaxHull)`, clamped to
`MinRepairLunations`..`MaxRepairLunations` (1 to 4, both exported). Against the
live 29 Hull castle:

| Hull at park | Lunations exposed |
|---|---|
| 29 / 29 | 1 |
| 22 / 29 | 1 |
| 15 / 29 | 2 |
| 7 / 29 | 4 |

At `ExhaustionDamagePerStep` 10, two tiles past a dry furnace costs a third of a
12-lunation cycle sitting exposed. Steep on purpose, and tunable from the
inspector without a rebuild.

**Exposure has teeth through machinery that already exists.** While
`CastleRepairLunations > 0`, the castle's StagingPoint is `Available = false`,
so the deploy drawer will not launch from it. The repair clock therefore gates
the next sortie directly, and `CycleState.CastleExposed` is the flag an attack
roll will read.

**The teleport resolves the banking question.** 2b had a field party carrying
the hold home. Ruling 4 supersedes that: the work crews carry it. When
`TickCastleRepair` pays the last lunation, the hold banks and a line lands in
`PendingSiegeReports` so the player reads it on returning to the map. A recall
still banks too, since a docked castle obviously unloads.

**Loop, end to end:** burn fuel, hit zero, choose camp or Hull, park, the
staging point goes dark for 1 to 4 lunations while crews work and the castle
sits exposed, the clock pays out, the hold comes home, the beacon relights, and
the next sortie launches from there with a full tank.

**Built but not yet dangerous:** nothing attacks the exposed castle yet. That
needs the attack-source ruling still open in the handoff (scripted, kingdom
hostility, or patrols at strategic scale). `CastleExposed` is the hook waiting
for it, and `CombatManager.CastleDefense` is the fight it should route to.
**Not built:** the teleport VFX. The moments are wired and logged; the ritual
the player watches is a presentation pass.

Verified: em dash gate clean; balance unchanged against HEAD on all four edited
files and absolute on the new one; no orphaned `_parkButton` references; the
repair curve checked numerically.

## Increment 2d: what comes for the castle

Ruled: all three sources, each with its own character.
Scripted drives narrative. Kingdom hostility forces aggression. Patrols hit and
run to deprive the enemy of resources.

**`Scripts/Systems/Strategic/CastleThreats.cs`** (294 lines). Rolled once per
exposed lunation from `RunLunationTick`, BEFORE the resupply ticks down, so a
lunation the camp was attacked in is work lost rather than work banked.

**Why the stakes are repair lunations and hold contents, not Hull.** Hull lives
on `ExpeditionManager` for the duration of a sortie and is not persisted between
them, so a parked castle has no Hull number to damage. Repair lunations and the
hold are the two things that survive in `CycleState`, they are already the
currency parking is priced in, and setting the resupply back is precisely what
"the crews were scattered" means. No new persisted stat was invented to say what
the existing two already say.

**Patrol raid.** Abstract, no tactical combat: a hit and run that resolves in
one beat is the point. The crew rolls to drive it off first at 25 percent each,
which is what makes leaving the fortress thinly staffed cost something. Failing
that, 25 percent of each resource in the hold walks away.

**Kingdom assault.** The strategic cost lands immediately (+2 resupply
lunations, 15 percent of the hold burned) and `PendingCastleAssaultKingdomId`
records that a tactical defence is still owed. An unanswered assault therefore
sets the player back rather than letting them off.

**Rolls are deterministic** per lunation, parking tile and repair state, seeded
from the world seed. Reloading cannot reroll a raid away, because save-scumming
a threat table is the fastest way to make one meaningless.

### The balance problem the simulation caught

First cut, 40,000 trials, assaults stacking freely:

| Stance | bill 1 | bill 2 | bill 4 |
|---|---|---|---|
| Friendly | 1.1 | 2.3 | 4.5 |
| Neutral | 1.4 | 2.8 | 5.7 |
| Hostile | 2.5 | 5.0 | **10.0** |

Ten of a twelve-lunation cycle. Not a setback, a lost timeline, and the player
cannot act their way out because being parked is the very thing drawing the
attacks. Each exposed lunation could add +2 against only -1 ticking off, so it
was close to a random walk.

A ceiling alone barely helped (10.0 to 9.2). The fix that worked is a design
rule rather than a number: **a second assault never lands while the first is
unanswered.** An assault owes the player a fight, and stacking another on top
punishes them for a debt they have not been given the chance to pay. Only raids
can land in the meantime, and raids cost resources rather than time.

| Stance | bill 1 | bill 2 | bill 4 |
|---|---|---|---|
| Friendly | 1.1 | 2.2 | 4.4 |
| Neutral | 1.3 | 2.6 | 5.0 |
| Hostile | 1.6 | 3.0 | **5.5** |

Bounded, and the character of hostile ground changed rather than vanishing: it
now costs GOODS (raids keep landing) and owes a FIGHT, instead of eating the
calendar. `MaxTotalRepairLunations` 6 stays as a belt-and-braces ceiling.

**Debug levers:** "Force Castle Raid" and "Force Castle Assault" run the same
resolution the lunation roll runs, so a forced threat costs exactly what an
organic one does. No lever reimplements game logic.

**Still a seam:** `PendingCastleAssaultKingdomId` is set but nothing routes it
to `CombatManager.CastleDefense` yet. The strategic cost is real; the tactical
fight is the next piece, and it should follow the `PendingWarfrontId` round-trip
pattern that already works.

## Increment 2e: the owed fight becomes reachable

`PendingCastleAssaultKingdomId` now routes to a real tactical castle defence,
following the `PendingCityFightCityId` round-trip that already works.

**Nothing new was built for the fight itself.** `CastleDefenseCompiler.Arm`
builds the map for the castle's terrain and installed modules, attaches the
protect objective with the Castle Heart, and the wizard arrives by waystone.
That was the whole point of routing through it: the ambush defence shipped in
August is now reachable from the strategic map instead of only from a patrol
interception mid-sortie. A kingdom sends soldiers rather than opportunists, so
the pull is `EncounterTier.Siege` rather than the Ambush pool a wandering patrol
draws from.

**Three ways it can end.**

- **Hold the camp and win.** The crews go straight back to work, so the
  `AssaultRepairSetback` the assault bought is handed back, floored at one
  lunation because the fight still cost a beat.
- **Hold the camp and lose.** The camp is overrun: +2 resupply, capped.
- **Withdraw.** The castle breaks camp under fire: +1 resupply, the assault is
  written off, no fight. A real option with a real price, which is what keeps a
  losing fight from being mandatory.

**Two bugs caught in review rather than in play.**

1. `Math.Min` / `Math.Max` do not resolve in `StrategicView`: the file imports
   only `Godot` and `System.Collections.Generic`, and uses Godot's `Mathf`
   throughout. All three sites converted. Worth remembering for this file.
2. Escape and the dialog's close box both emit `Canceled`, so a stray keypress
   would have withdrawn and spent a lunation. Withdrawing now hangs off the
   cancel BUTTON's own `Pressed` signal; `Canceled` just frees the dialog. An
   unanswered prompt therefore defers, leaving the assault owed and re-offered
   on the next map load, which is the harmless reading of "the player did not
   answer".

**`CastleDefenseLaunched`** distinguishes "a fight is owed" from "a fight is
being fought", so a mid-combat reload leaves the assault owed rather than
silently resolving it. Same discipline as the district fight's stale-record
guard.

**Known rough edge:** the withdraw report lands in `PendingSiegeReports` and is
read by the HUD on its next build, not immediately. `BuildHud` is not written to
be re-entered and this fires from inside a dialog callback, so forcing a refresh
there was not worth the risk. If the delay reads badly in play, the fix is a
toast, not a HUD rebuild.

## Increment 2f: the castle stops being a pawn

`ExpeditionWindow3D.BuildPawn` built a cylinder, a sphere and a lamp. The
Mobile Fortress spec section 2.3 said the pawn becomes the castle model and that
was never done, so for a month the thing the player drives has looked like a
chess piece.

`BuildCastleToken` replaces it, built from the SAME vocabulary the combat
scene's castle uses: six-sided prisms rotated 30 degrees, the hull / leg / stack
tints lifted from `SpawnCastleBodyStamp`, hull over legs under stacks. The
fortress the player drives is now recognisably the fortress they defend in a
castle defence, which matters more than either one looking good alone.

Procedural on purpose. The combat castle is placeholder geometry too, so both
can be swapped for a sculpted mesh at the same time rather than drifting apart
while one waits on Blender.

The school tints the Heart and the lantern, which is the one thing that makes a
Cinderhold read differently from an Ossuary Ambulant at a glance.
`CastleTypeDef` already named the chassis per school; now it has a colour.
Only the castle carries light: a field party is people on foot, and a second
`OmniLight3D` at range 7 would look wrong and cost twice as much to render.

`Prism` is a shared helper, so the field party token is a sibling method rather
than a second geometry system.

## Increment 2g: the march, and the castle stops working POIs

### The castle no longer works POIs (ruled)

`IsEncounterPoi` already drew the line the stride planner needed (Combat,
Narrative, Negotiation, Prison, Objective versus the logistical kinds), so it
draws it on arrival too rather than a second list drifting out of step. Stepping
onto an encounter POI now CHARTS it and marches on: the site is marked
Discovered, stays UNCONSUMED so a field dive can still take it, and the lens
reports what it found.

Both stride halts on encounter POIs are gone. The castle cannot work one, so
stopping to look at it was pure friction. `StridePoiPenalty` stays, because
encounter ground draws patrols and routing wide of it is still worth the fuel.

Logistics are deliberately still the castle's: rest sites, outposts, caches,
seats and settlements are how a fortress refuels and opens the map, not content
to be explored. Cutting those too would gut the section 3.2 fuel economy with
nothing standing in for it.

### The march

`Scripts/Systems/Strategic/CastleMarch.cs`. A second movement mode rather than a
replacement, and the two have deliberately different characters:

| | March | Stride |
|---|---|---|
| Cost | flat 2 fuel/tile | real per-edge terrain cost |
| Terrain | ignored | the whole economy |
| Encounters | none | patrols, ambush, weather |
| Fog | Charted corridor, radius 2 | revealed tile by tile |
| Watchable | no | yes, 0.25s per tile |
| Ends | parked, 1 lunation of camp | wherever you stop |

The flat cost is the point, not a shortcut. A strategic move that priced every
edge would just be a stride the player cannot watch, and the terrain economy is
what makes stride worth having. The corridor is Charted only, never Explored, so
a marched route still hides its POIs and the exploration keystone survives.

**Persisted fuel** was the missing piece: a march is ordered from the strategic
map, where no `ExpeditionManager` exists to own `StepsRemaining`.
`CycleState.CastleFuel` / `CastleMaxFuel` are written when the castle parks and
refilled when the resupply completes, which is what makes the repair clock the
only gate on the next move.

**Cost in practice:** MaxFuel 40 buys a 20-tile march on a 158x96 world. Chaining
them costs one exposed lunation each, so range is cheap and risk is not.

**The control** sits on the deploy drawer beside Deploy, because the question is
identical ("go here") and the two answers belong side by side. It appears only
while the castle is parked, and when a march is illegal the button is disabled
with the refusal as its tooltip, so the player learns the rule from the button
rather than from a dialog after the fact.

**Caught in review:** `CommitCastleMarch` took a `Control`, but `CloseDeploy`
takes a `PanelContainer`. Would not have compiled.

**Known rough edge:** committing a march calls `ReloadCurrentScene`. The castle's
staging point, the beacons, the fog and the markers all moved, and rebuilding is
honest where patching four places by hand would drift. If the reload reads badly
in play, the fix is a targeted refresh, not a patch-up.

## Increment 2h: two bugs from playtest

### The tank was filled before it was sized

`StepsRemaining` was set to `OperatingRange + BonusSteps`, and only AFTER that
did `MaxFuel` fold in the castle chassis (`_castle.BonusMaxFuel`) and the Furnace
crew station (`_crew.BonusMaxFuel`). So every sortie began below capacity and
every +MaxFuel bonus in the game was dead weight.

The Adept's Bastion Errant is the clearest casualty: its entire operating quirk
is "+5 MaxFuel, the generalist's deeper tank", and it handed the player a tank
they started five short in. That is the 40/45 seen in the playtest. A Commander
at the Furnace would have been lost the same way.

Fixed by filling the tank once every contribution is in. The combat-return path
restores `SavedStepsRemaining` further down and is unaffected.

### Docking un-parked the castle, so March never appeared

`UnparkCastle` set `CastleParked = false`. But the March control is gated on
`CastleParked`, so it could not appear until the player had run a tank dry and
camped in the field. On a fresh cycle the castle also had `CastleX/Y` at -1, so
there was no anchor, no march range and no readout at all.

The modelling was wrong, not just the flag. **Parked means "stationary on the
world and able to act"**, not "camped in the field". A docked castle is
stationary and can march. What ends at the dock is the field CAMP: the castle's
own staging point is retired (the dock has its own), the resupply is settled by
the turnaround, and the furnace is refilled.

`EnsureCastleSited` now puts the castle at the dock on the first strategic load
of a cycle, idempotently, with a provisional tank that ExpeditionManager
overwrites with the real capacity on the first park or dock. The castle anchor
is also suppressed when the castle stands on ground that already stages, so the
dock does not list twice.

### On the field party, which is still not built

Asked for a third time and still owed: deploying the party from the castle, and
sending it to a discovered outpost.

The good news from reading `Deploy()`: it is already parameterised. It sets
`PlayerSession.ExpeditionStagingCol/Row` and `ExpeditionWindowRadius` and changes
scene. A party dive is the same call with a smaller radius and a different
roster, not a parallel system. What it needs:

1. A run-kind flag on `PlayerSession` (castle sortie vs field dive).
2. `ActivePartyCompanions()` to read the field party roster when diving.
3. `BuildCastleToken` to build the party token instead (the `Prism` helper is
   already shared for exactly this).
4. Castle-only setup skipped on a dive: chassis quirks, crew stations, the
   furnace gauge.
5. "Send field party" on the deploy drawer beside Deploy and March.

Deliberately NOT started in the same pass as the two fixes above. Both fixes
land in hot paths (`Deploy`, the resource block, the strategic load) and neither
has been through the engine yet. Stacking a third feature on two unverified
fixes is how a build session produces three things to debug at once instead of
one.

## Increment 2i: a finished run could still be walked

Reported: after the return button appeared, the castle still moved, burned no
fuel, and could explore the map without limit.

**One root cause, two symptoms.** `OnWindow3DMove` was the only movement entry
point without an `ExpeditionComplete` guard. `OnHexClicked` (the 2D path) has
one and `BeginStride` has one, so distant clicks were already refused; the
ADJACENT branch was not, and it calls `_party.TryMoveTo` directly.

The free exploration follows from the same end state rather than being a second
bug. The player had parked with a dry furnace, so every further step took the
Hull branch instead of the fuel branch. That branch ends in a Hull-zero forced
recall, and `EmergencyExtract` early-returns when the run is already complete.
So Hull pinned at zero, nothing else charged, and the castle could walk the
world for free.

Pre-existing (the 3D view shipped in August with the gap), but parking made it
easy to reach: extraction usually happened at an anchor, while parking is a new
end state reached in open country with the map still in front of you.

Three fixes, narrowest first:

1. `OnWindow3DMove` refuses when the run is over. This alone closes it.
2. `OnPartyMoved` refuses too. It is where fuel, Hull, leash, weather and
   corruption are all charged, so it should not trust every caller forever.
3. `ShowReturnButton` switches the 3D view's input off, and the container's
   MouseEntered re-arm now respects `ExpeditionComplete`, so a stray mouse-over
   cannot switch it back. Silently swallowing clicks reads as a broken game;
   refusing them at the source takes the hover hints down with it.

All four movement entry points verified guarded.

## Increment 2j: one order pipeline, and the 3D view stops being second class

Ruled: the 2D map is no longer used, so the 3D view is the game.

### The durable fix

Three bugs of the same shape in four reports, so the guard became a predicate
instead of a habit. `CanAcceptOrders()` is the single answer to "is the
expedition taking orders", and `HandleTileOrder(local)` is the single pipeline
both views funnel into. Four independent checks that each had to be remembered
was not a design, it was a list of future bugs.

Order inside the pipeline is deliberate: cancel a stride first (section 3.4: a
click mid-march is the one order accepted), then spell targeting, because an
armed spell means the click was aimed at the world rather than at a destination,
then movement, adjacent walking and distant marching.

Side effect worth noting: 2D gains stride orders, which it never had. It was
calling `TryMoveTo` on any tile regardless of distance.

### The bug the audit found, which was worse than the one reported

`OverworldSpellManager.HandleHexClicked` had exactly ONE caller: the 2D click
handler. **Every tile-targeted Grimoire spell was dead in the 3D view.** The
click moved the castle or started a march instead. The Grimoire panel is on
screen in 3D, fully interactive, and arming a spell then clicking a tile did
something unrelated.

That has been live since the 3D view shipped, and it would never have surfaced
from the movement report alone.

### And the drift behind it

Targeting highlights are drawn as `Polygon2D` children of 2D hex nodes. The 3D
view has no hex nodes, so fixing the click alone would have made targeting
functional but invisible, which is arguably worse than dead.

`TargetTilesChanged` is a new injected hook on the spell manager, matching
`TileQuery` / `FogQuery` / `StrideLockQuery` beside it, carrying the range set
and the drawn path. `ExpeditionWindow3D.ShowTargetTiles` paints both as unshaded
no-depth-test discs, the same trick the stride ribbon uses to stay readable over
any terrain, with the path brighter than the candidates.

A sweep for the same pattern (`hex.AddChild`) found three painters:
`OverworldPartyToken` move hints (already mirrored by `RebuildMoveHints`), the
targeting highlight and the path highlight (both now mirrored). Nothing else
paints onto hex nodes.

## Increment 2k: the regression I shipped with the unification

Reported: in the 3D view, clicking ANY tile walked the castle up and to the left.

### Cause

Not the coordinate conversion, and not the spell call. `OnWindow3DMove` is
byte-for-byte equivalent to what it replaced, and `HandleHexClicked` returns
false when idle.

The cause was a change I made that nobody asked for. Unifying the pipeline gave
the 2D handler STRIDE ORDERS, which it never had, and I wrote that up as a free
improvement to a view the player had just said they do not use.

It was not free, because that path is still wired. The 2D grid stays alive under
the 3D view as the MODEL: `_grid.Hexes`, `_grid.Distance` and
`_party.CurrentCoord` are what the whole expedition reasons about, including the
3D view. Its Area2D hexes can still emit `HexClicked`.

While `OnHexClicked` only ever called `TryMoveTo`, a stray 2D click was harmless:
`TryMoveTo` requires adjacency, so it failed silently. The moment it could also
call `BeginStride`, a stray click stopped failing and became a march toward
whichever hex sat under the cursor in 2D SCREEN space. That is a roughly fixed
direction from the party, which is exactly the reported symptom.

### Fix

`OnHexClicked` refuses while `_window3D != null`. Only the ACTIVE view issues
orders. `OverworldPartyToken` was checked and handles no input of its own, so
`OnHexClicked` was the only other order source.

### The lesson, which is not "add a third guard"

The previous entry congratulated itself on replacing four scattered guards with
one predicate, and in the same change introduced a fifth failure mode by
extending capability to a dormant path. A predicate answers "may orders be
issued at all". It does not answer "is this view the one in charge", and those
are different questions.

Two rules worth keeping:
- Only the active view issues orders.
- An improvement to a code path nobody uses is not free while that path is
  still connected. Either gate it or disconnect it, but do not widen it.

## Increment 2l: legible damage, and a march you can actually order

Confirmed working first: 3D movement and 3D spell targeting.

### The castle bled with no explanation

The drain fired `ShowInfo("Hazardous terrain! The castle takes N Hull damage.")`,
which names neither the ground nor the way out, and nothing warned BEFORE the
step. In a swamp at 3 Hull per tile that reads as the game damaging you at
random.

Three changes, and the ordering of them is the point: the player should learn
the rule before paying for it.

1. **The move hints carry a hazard pip.** Ground that eats Hull gets a red dot
   in the middle of the hint ring. The ring's COLOUR already encodes fuel cost,
   so hazard needed its own channel rather than competing for the same one.
2. **The tooltip states the toll**: "Swamp  ·  3 Hull per step". Both tooltip
   builders carry it, since the hover path and the per-frame refresh path both
   reach the player.
3. **The message names the ground and the escape**: "The swamp's miasma eats at
   the hull: 3 Hull lost. Roads and wards spare you this." The road exemption
   (S4.2) and terrain wards already existed and were invisible.

**Trap avoided:** display must never call `TerrainHPDrain`. Volcanic rolls a die
inside it (30 percent for 5), so a hint built from it would flicker between
"safe" and "5 Hull" on every redraw. Added `TerrainDrainsHull` (pure predicate)
and `TerrainDrainNote` / `TerrainDrainProse` beside it, and Volcanic honestly
reads "may erupt, 5 Hull" rather than pretending to be a flat toll.

### The march was unusable, structurally

It only appeared on the deploy drawer for an EXISTING staging point, and a fresh
cycle has exactly one: the tile the castle is already standing on. So there was
nowhere to march to. The whole point of repositioning is going somewhere that is
NOT already a launch point.

**March mode.** A toggle on the strategic HUD, then click any charted ground.
Confirms with the real cost before committing, and refuses out loud rather than
swallowing the click, because a player who armed a verb and clicked reads a
silent no-op as broken.

`CanMarchAtAll` splits the castle-state preconditions (parked, resupplied, no
assault owed, fuel for at least one tile) from the destination checks, so the
toggle can refuse to ARM with a reason instead of accepting the arm and then
rejecting every tile. `ExecuteCastleMarch(col, row, panel)` is now shared by
march mode and the drawer button, so the two cannot diverge.

**Caught while wiring:** the march branch first went in ABOVE the city-view
guard, which would have hijacked clicks meant to descend into a settlement. A
world-map verb belongs after the city has had its say, and the button hides in
city view for the same reason.

## Increment 2m: the march cost was wrong, and the evidence was already in the repo

Reported: a discovered, explored second deploy node could not be marched to for
want of fuel.

Not short fuel. A wrong number, and one I could have checked before shipping it.

`WorldGenerator` seeds the bootstrap outposts by ACTUAL walked step cost, on
purpose, with the reasoning written in a comment: Frontier at 18 to 24 ("about
half the budget one-way"), Distant at 28 to 34 ("reachable one-way at 70 to 85
percent of budget"), against a 40 to 45 tank. A recorded world put them at:

| Outpost | hex distance | walked cost | march at 2 | march at 1 |
|---|---|---|---|---|
| Frontier | 16 | 19 | 32 | 16 |
| Distant | 21 | 34 | **42** | 21 |

So the real cost is 1.19 and 1.62 fuel per hex of straight-line distance. I had
written `FuelPerTile = 2` and justified it in the doc comment as "roughly the
average step cost, so marching is neither a discount nor a tax". That was an
assumption, never checked, and wrong in the expensive direction: the march cost
32 to reach ground worth 19 to walk to, and the Distant outpost at 42 was simply
unreachable on a full tank.

Worse than the arithmetic, it inverted the design. The ABSTRACT option was
dearer than the detailed one, so there was never a reason to march.

**`FuelPerTile` is now 1.** A full tank reaches 40 to 45 tiles, both bootstrap
outposts are comfortably in range, and the march is cheaper than striding the
same ground (16 against 19, 21 against 34). That is the trade it was always
meant to be: pay less fuel, give up the exploring, the encounters, and the
ability to act on anything you pass.

The refusal message now also names the range you DO have, rather than only the
cost you cannot afford.

**Related inconsistency fixed:** `CastleMaxFuel` was written only when the castle
parked. A player who always extracts kept the provisional 40 that
`EnsureCastleSited` seeds, and was refuelled to that instead of their true
capacity (45 for an Adept). It is now recorded at deploy, the moment the real
tank is known.

**The lesson:** the number was checkable. `WorldGenerator` states its distance
bands and its reasoning in a comment forty lines long, and a recorded playtest
log had the actual figures in it. I wrote a constant from intuition and
described the intuition as if it were a measurement.

## Increment 2n: take the road

### Measured before built, for once

`Roads` lays a MINIMUM SPANNING TREE over the settlements (union-find, links
stamped shortest-first). That answers the pacing question directly:

- 48 links over 50 settlements, so average settlement degree 1.92, no cycles.
- Forks therefore come from two places only: settlements of degree three or
  more, and mid-route merges where a later link snaps onto an existing road and
  branches off (the A* reads current road edges, which is what builds trunks).
- 516 road edges over 48 links is about eleven tiles per link.

So halting on settlements AND on any tile with three or more road edges paces at
a few halts per tank. Junctions alone would have been too sparse; settlements
alone would miss the trunk branches.

### A stride, not a march, and not "until dry"

The order is a STRIDE. It costs real fuel, patrols can catch it, weather bites,
and every existing halt still applies. Making it a march would have handed the
player the map: the march is abstract and safe, roads link settlements, and
settlements are where staging, services and courts live, so a free road-follow
would be an escalator to the best things in the world.

It does not run until the furnace is dry either. Dry means park, and a park is
an exposed camp, possibly in a kingdom the player has never seen. Defaulting
into that is a trap rather than a decision, so it halts wherever a decision
exists and the player re-issues.

Implementation is small because the stride loop already owns everything that
matters. `_roadFollow` swaps the next-tile chooser (`TryNextRoadTile`: the road
edge you did not arrive by) and adds one halt rule (`RoadFollowShouldHalt`).
The arrival check and the lost-bearing check are skipped, since a road-follow
has no destination and no bearing to lose. `EndStride` clears the flag.

The control appears only with a road underfoot, so it teaches its own
precondition instead of sitting dead. HUD row four: Extract 12, Ledger 60,
Switch to 2D 108, this 156. Checked against the others this time, after the Park
button shipped at 58 into the Ledger's 60.

### Not built, and it is the half that matters

**Patrols do not favour roads.** Right now roads are strictly better: S4.2 spares
them the terrain's bite and the supply line's drag, the discount floors step cost
at 1, and now they guide you through fog. Nothing offsets that. The counterweight
is that a road is the OBVIOUS route, so it should be the watched one, especially
in hostile kingdoms. Until that lands, road-follow is a safe fast lane rather
than a trade, and the tooltip currently promises a danger the simulation does not
deliver.

### Tooling note

Five edits printed OK and none of them were saved. The patch helper accumulated
into a string and wrote once at the end, so a later assertion threw before the
write and silently discarded the successful ones, leaving a field reference with
no field. It now writes after every edit. Worth remembering: a batch that reports
success per item but commits once can report five successes and commit nothing.

## Increment 2o: patrols watch the roads

The counterweight the last increment shipped without. Roads were strictly
better: S4.2 spares a road step the terrain's bite and the supply line's drag,
the discount floors step cost at 1, and road-follow now guides the castle through
fog. Nothing offset any of it, so there was no trade, only a fast lane.

A road is the OBVIOUS route. It should be the watched one.

### Measured, both halves

**Density.** The generator lays about 528 road tiles (48 links, roughly 11 tiles
each) over about 9,100 land tiles: roads are 5.8 percent of walkable ground, and
a radius-12 window holds about 27 of them.

**Spawn.** A road tile now enters the spawn pool `RoadSpawnWeight` (10) times.
That is 270 weighted entries against roughly 442 ordinary ones, so about 38
percent of patrols start on a road. No separate road list and no special-casing
downstream: the spacing filter and the deterministic sort both still work,
because duplicates share a coordinate and are excluded together once one is
placed.

**Wander.** Spawn bias alone would put patrols near roads and then let them drift
off. `Wander` now weights road neighbours, harder when the patrol is already on
one (8 against 4), so a patrol that reaches a road walks it.

Simulated rather than asserted, on a synthetic window with roads laid as winding
trunk lines:

| weights | time on roads |
|---|---|
| unweighted (before) | 11.3% |
| **shipped, on 8 / off 4** | **45.3%** |
| on 4 / off 2 | 27.0% |
| on 16 / off 8 | 65.0% |

The unweighted run matching the synthetic density (11.3 against 11.2 percent) is
the sanity check that the walk itself is unbiased. The shipped weights give a
FOUR-fold concentration. The synthetic map is about twice as road-dense as the
real one, so the absolute figure will be lower in play, around 23 percent of
patrol time on roads against a 5.8 percent baseline. The concentration ratio is
the transferable number, not the percentage.

Tunable from two named constants in each file if that reads too hot or too cold.

The road-follow tooltip already promised "patrols watch them". It is now true.

### Deliberately not done

No hostile-kingdom multiplier on the spawn weight. It would need a region-to-
kingdom lookup and a stance query inside the spawn path, and the effect largely
falls out already: patrols drift home to their own territory, so marching a road
through someone's land is where you meet them. Worth revisiting only if play says
hostile ground does not feel more watched than friendly ground.

## Em dash purge, second pass

`CLAUDE.md` requires both em dash greps to stay empty. They were not: 64
occurrences across 12 files, all introduced by 88ab403 ("Diplomacy version 2").
Rewritten this session, 63 edits (one line carried two). Both gates are clean
again, and no en dash or hyphen was substituted anywhere in prose.

Where the connectives landed:

- **NPC dialogue** (15 in `NegotiationBarks.cs`, 1 in `NegotiationManager.cs`).
  An ellipsis where the pause IS the performance: "But… agreed", "That… yes",
  "in a port like this… you've done well". A comma where the dash was only
  glue: "Between us, the {want} is what I want". The Merchant's deadpan
  became its own sentence, which lands the joke harder than the dash did:
  "Doesn't change the numbers. Much."
- **UI strings and tooltips** (about 20 in `NegotiationManager.cs`). A colon
  where a label introduces its explanation ("Trade value: what it's worth to
  you", "LOCKED: YOURS", "sealed: only signs if they're Warm"), a full stop
  where two statements were jammed together ("Clock held. Your next card
  won't cost patience").
- **Detail log lead-ins** (2 in `NegotiationState.cs`). The dash was doing rule
  duty at the head of a line whose fields are already separated by a middle
  dot, so the middle dot leads too.
- **Bare placeholder glyphs** (2 in `NegotiationManager.cs`). The two markers
  that were a lone em dash in a string literal became plain hyphens, which is
  the one exception the style rule allows.
- **Comments and doc comments** (the remainder). Colons, full stops and commas
  per the rule. No banner rule characters needed re-padding: every hit was in
  running prose, not on a ruled line.

Verified: both greps clean; zero en dashes added; brace, paren and bracket
balance compared against HEAD per file and unchanged everywhere, so no edit
touched structure. The only non-ASCII characters on added lines are box rules,
smart quotes, ellipses, middle dots and the existing UI glyphs.

## Unrelated finding

`FracturedArcana.csproj` shows an uncommitted `Godot.NET.Sdk` bump from 4.6.1
to 4.6.2, which this session did not make. The editor appears to have written
it on open. Project notes still say 4.6.1, so either commit the bump
deliberately or revert it before it rides along in someone else's commit.

---

# Increment 2p: road-follow direction control

## The defect

"Take the Road" had no steering. `TryNextRoadTile` walked the six-bit
`RoadEdges` mask in `HexCoord.AxialDirections` order and took the first set
bit, which is a fixed compass preference dressed up as a decision: from any
junction the castle always left the same way. Direction order is
`(1,0) (1,-1) (0,-1) (-1,0) (-1,1) (0,1)`, so on a flat-top odd-q map the bias
ran southeast first, then northeast, then north.

The second half of the same defect: `RoadFollowShouldHalt` printed "The road
forks. Choose a way on." and then handed the player nothing to choose with.
Re-issuing the order hit the same first-set-bit chooser, so the fork halt was
decorative. Same class of dishonest UI as the patrol tooltip fixed in 2o.

## The fix

One mechanism covers both cases.

- `RoadExits()` collects every road edge that leads somewhere loaded and dry.
- `OfferRoadChoice(cameFrom)`: zero exits refuses; ONE exit goes immediately
  (a prompt with one option is a click tax, not a decision); two or more arm
  the pick.
- The pick highlights each option in the 3D view with a floating label naming
  where that branch leads, and the next click chooses. The chosen tile is
  written to `_roadSeed`, a one-shot override consumed by `TryNextRoadTile`
  on the first hop. After that the corridor has exactly one non-backtracking
  answer per tile, so the seed is never needed again.
- A fork halt calls `OfferRoadChoice(_strideLastTile)`, so the halt text is
  now true in the same beat rather than leaving the castle parked at a
  junction while the player guesses that a button would help.

## Decisions worth recording

**Labels, not compass bearings.** The 3D camera orbits on Q and E. A bearing
label would have been about fifteen lines cheaper and would have started
lying the moment the player pressed E. The discs carry direction; the labels
carry destination.

**Labels are fog-gated.** `RoadBranchLabel` traces a branch up to
`RoadPeekTiles = 20` (the generator puts roughly eleven tiles between
settlements, so twenty reaches the next one on a normal link) and stops at the
first unrevealed tile, the next settlement, a further fork, or the road's end.
An unrevealed settlement is never named. Ungated, the pick would have been a
free scouting report and road-follow would have become exactly the escalator
to the best things on the map that the order was designed not to be.

The label walk refuses water the same way `TryNextRoadTile` does, so a branch
can never be named by a route the castle would not take.

**Backtrack is offered, not hidden.** At a fork, one exit is where the castle
came from. It is listed and labelled "back the way you came" rather than
filtered out. Turning around at a fork is a real order, and an option silently
removed is worse than one the player can see and skip.

**The pick is not a trap.** A click off the highlighted tiles drops the pick
and falls through to whatever that click would normally have meant. Arming a
spell drops it too, because both highlights draw into the same `_targetTiles`
node list and an invisible pick that still eats clicks is the worst outcome
available. Pressing the button again cancels. The road sliding out from under
the castle cancels.

## Touched

- `UITheme.cs`: `RoadChoiceHighlight` (Gold) and `RoadChoiceOutline`
  (WorldDeep). Deliberately NOT the spell blue: aiming a spell and choosing a
  way on must never be mistaken for each other on the same board.
- `ExpeditionWindow3D.cs`: `ShowRoadChoices(List<(Vector2I, string)>)`, discs
  plus billboarded `Label3D`, pushed into `_targetTiles` so `ClearTargetTiles`
  frees them.
- `ExpeditionManager.cs`: `_roadPick`, `_roadSeed`, `_roadPickOptions`,
  `RoadPeekTiles`; `RoadExits`, `OfferRoadChoice`, `RoadBranchLabel`,
  `CancelRoadPick`, `StartRoadFollow`; `BeginRoadFollow` reduced to a toggle;
  `RoadFollowShouldHalt` gained `out bool refork`; `TryNextRoadTile` consumes
  the seed; `EndStride` clears it; `HandleTileOrder` consumes the pick click
  first; `TargetTilesChanged` cancels the pick; `RefreshHud` cancels it when
  the road is gone; tooltip updated.

## Verification

Brace, paren and bracket balance zero on all three files. Em-dash greps clean
(both forms, whole tree). No compiler available on this machine, so the build
is unverified: this increment needs an editor pass before the field party goes
on top of it.

---

# Increment 2q: road-pick legibility, and two bugs the screenshot exposed

Reported from a play screenshot: "the labels clip pretty severely with the
terrain." Looking at the shot turned up three separate faults, only one of
which was the reported one.

## 1. Prose on the ground was never going to work

Road junctions are ADJACENT TILES by definition. Three labels of a dozen
characters each, drawn at a uniform height over three neighbouring hexes, will
overlap one another and sprawl across the scatter between them. `NoDepthTest`
kept them from being occluded, so the failure mode was not clipping in the
z-buffer sense: it was three overlapping sentences painted over the trees.

Length is the whole problem, so the fix is to remove the length. Each option
now gets a disc, a pin standing out of it, and a single DIGIT on top. The
sentence moves to the HUD info line, keyed by that digit:

    The road runs 3 ways. 1: a city, 6 on  2: unexplored ground, 4 on
    3: back the way you came. Click one.

A single character cannot collide with itself, and the HUD line already had
the width to spend.

Pins are STAGGERED by list order (`RoadPinBase 1.7`, `RoadPinStagger 0.6`).
Uniform heights over neighbouring tiles still converge on screen when the
camera drops toward the horizon, which is where this view spends most of its
time. Different heights cannot.

The base height is measured rather than guessed, which is the standing lesson
from the `FuelPerTile` mistake: `PainterlyProps.ConiferCanopy` tops out at
y 0.88 + 0.38 = 1.26 at unit scale, and the forest scatter scales it by 0.55
to 1.10, so the tallest tree reaches about 1.39. 1.7 clears it with room for
the label outline.

## 2. Every branch read "a settlement, 1 on"

Visible in the same screenshot and worth more than the layout problem, because
a label that is legible and wrong is worse than one that is unreadable.

`Settlements.Claim` stamps `SettlementIndex` onto EVERY TILE of a settlement's
footprint, not just its centre. `WorldData.SettlementAt` reads that stamp. So
asking it about a tile one step out of Ashfeld Crossing answered "Ashfeld
Crossing" for all three roads out of it: the label was reporting the
settlement the castle was already standing in.

`RoadBranchLabel` now captures the footprint index at the origin tile and only
reports a footprint DIFFERENT from it.

Worldgen never assigns `WorldSettlement.Name` (checked: no assignment anywhere
in `Settlements.cs`), so the name branch is dead until a naming pass lands.
New `SettlementLabel` falls through to the tier instead, which is the axis the
player is actually choosing between: "a city (staging)", "a city", "a town".
Cities grant staging and towns do not, so that distinction is the whole reason
to prefer one road over another. If names arrive later they take over with no
other change.

## 3. The march halted on every tile of a settlement

Found while fixing 2, never reported because nobody had marched into a town
yet. `RoadFollowShouldHalt` asked `SettlementAt` about the tile underfoot and
halted whenever it answered. A nine-tile footprint therefore halted the march
NINE TIMES, once per step, and re-issuing walked exactly one tile and halted
again.

Now halts on ENTRY: `_roadSettlementIndex` tracks which footprint the march is
inside, seeded at the order from the tile the castle starts on (so beginning a
march inside a town does not immediately announce that town), and the halt
fires only when the index changes to a different valid footprint. Leaving and
returning still halts, which is correct.

## Touched

- `ExpeditionWindow3D.cs`: `ShowRoadChoices` rewritten to badges plus pins;
  `RoadPinBase` / `RoadPinStagger`.
- `ExpeditionManager.cs`: `OfferRoadChoice` builds numbered badges and HUD
  prose; `RoadBranchLabel` gained footprint identity; new `SettlementLabel`;
  `RoadFollowShouldHalt` halts on settlement entry; `_roadSettlementIndex`.

## Verification

Balance zero on all three files, em-dash greps clean tree-wide. Still no
compiler on this machine. Seven unverified changes are now stacked and the
next thing that happens should be a build.

---

# Increment 2r: letters instead of digits, and a halt card

## The badges were speaking the map's other language

Cropped and enlarged the reported screenshot rather than trusting the read at
full size, which is what turned this up. The white "1" and "2" glyphs scattered
around the castle are not stray road badges: they are `RebuildMoveHints`
painting `cost.ToString()` on every adjacent tile, `Label3D`, `FontSize 40`,
billboarded. My road badges were gold digits at `FontSize 46`, billboarded, on
adjacent tiles.

A road junction's options ARE adjacent tiles, so the pick was guaranteed to put
a gold "2" next to a white "2" that means a completely different thing. The map
already spends digits on fuel cost, so the pick cannot have them.

Badges are now LETTERS: A, B, C. Nothing on this map reads a letter as a
number, and the HUD line keys off the same letter.

This was not in the report. The reported complaint was layout; the alphabet
collision was the more serious of the two, because a badge that is legible and
means the wrong thing is worse than one that is hard to read.

## Halt card

Requested: a popup when the road-follow stops, and "probably better" on every
stop with a brief reason. Agreed, and the second version is the right one.

Every reason the castle stops already existed as a sentence and every one of
them went to `_infoLabel`, one grey line in a corner of a crowded HUD. The
march stops for eight distinct reasons and the player could not tell which.

`ShowHaltNotice(headline, detail, tone)` puts up a single card at bottom
centre, the one band of the screen the Grimoire (bottom left) and the toasts
(bottom right) leave alone. Headline, wrapped detail, and a state line.

**Not a dialog.** A fork halt re-arms the tile pick in the same beat, so a
modal would be standing in front of the thing it just told the player to click.
Every node in the card is `MouseFilter.Ignore` for the same reason, and the
card auto-dismisses after 7 s or when the next order starts.

The state line (`Fuel 38/40  Hull 29/29  Hills`) is on every card, because the
two questions that follow "why did it stop" are always "how much fuel is left"
and "how bad is the Hull", and having that answer three panels away is what
makes a halt feel arbitrary.

`_haltNoticeToken` guards the dismiss timer: without it, a second halt inside
the 7 s window would have the FIRST halt's timer close the SECOND halt's card
early.

### The eight halts, now named

| Headline | Tone | Detail |
| --- | --- | --- |
| Arrived | Neutral | reaches the ordered tile |
| Settlement reached | Neutral | names the city or town |
| The road forks | Choice | re-arms the pick, options listed on the card |
| The road ends | Neutral | dead end |
| Off the map | Neutral | ran out of charted ground |
| Hull spared | Trouble | Hull at a quarter, march stops rather than grind |
| Furnace spent | Trouble | quotes the next tile's cost against fuel held |
| No way onward | Trouble | nothing adjacent passable and unvisited |
| Blocked | Trouble | the move itself was refused |
| Bearing lost | Trouble | blind march stopped closing on the goal |
| Halted | Neutral | the player called it |

Tone drives the accent only (Gold / ArcaneBlue / Warning). The words do the
work.

`EndStride` gained `headline` and `tone` parameters, both defaulted, so the
four run-end `EndStride(null)` calls are untouched and silent.

A pending pick also writes its options onto the card rather than only the info
line, because three options run past the width of a HUD strip and the card
wraps.

## Touched

- `ExpeditionManager.cs`: `HaltTone`; `_haltNotice` / `_haltHeadline` /
  `_haltDetail` / `_haltState` / `_haltStyle` / `_haltNoticeToken`;
  `BuildHaltNotice`, `ShowHaltNotice`, `HideHaltNotice`; `EndStride` signature
  and all eight call sites; `RoadFollowShouldHalt` gained `out string title`;
  badges to letters; `HideHaltNotice` on new orders.

## Verification

Balance zero, em-dash greps clean tree-wide. Still no compiler here. EIGHT
unverified changes stacked now. Nothing else should be built before a build.

---

# Increment 3: the field party

The second force, asked for three times and deferred three times. The data
layer has been in place since increment 1; this is the half that makes it do
something.

## Five touch points, plus two nobody had counted

The plan was the five I had named. Two more turned up while building, and both
would have been live bugs.

### 1. Run kind (PlayerSession)

`ExpeditionRunKind` (Castle / Field) and `ExpeditionFieldPartyId`, handoff
statics beside `ExpeditionStagingCol`. Read once in `ExpeditionManager._Ready`
into `_fieldRun`, before `ClearRunState` runs.

A static and not a save field on purpose: it is a scene-change argument, and a
save field would make it look like durable state. It survives the combat
round-trip for free, which is exactly what a field run needs (combat returns
straight to ExpeditionScene through EncounterRouter, never via the strategic
map; checked, not assumed).

### 2. Roster (ExpeditionManager)

`ActiveRunCompanionIds()` is the one place that answers "who is out". A castle
sortie reads `ActivePartyCompanionIds`, which is what it has always meant. A
field run reads the named party's roster, which `ReconcilePostings` has already
rebuilt from the companions' own `Posting` fields.

`ComputePartyBaseHP` reads it too, so a field party of two does not walk into
POIs carrying the fortress crew's HP pool.

The castle-only deploy setup is now an if/else:

| | Castle | Field |
| --- | --- | --- |
| Chassis terrain signature | applied | **cleared** |
| Crew stations | auto-assigned | none, `CrewEffects.None` |
| Budget | `OperatingRange + campus + chassis + Furnace` | `FieldRations (18) + campus` |
| Chrono flat moves | seeded from chassis | **zeroed** |
| Writes `cycle.CastleMaxFuel` | yes | no |

The two **cleared** rows matter more than they look. `OverworldMovementCost`'s
castle ambients and `PlayerSession.ChronoFlatMovesLeft` are statics that
SURVIVE THE SCENE CHANGE. A field run that merely skipped setting them would
have silently inherited whatever the last castle sortie left behind: a
Gearspire's doubled road discount, an Enchanter's waived ford, free
Chronomancer steps. Skipping is not the same as clearing.

`_castle` stays assigned on both paths. It is a lookup, not state, and six
sites read it without a null guard; leaving it null would have traded one
branch here for six crashes elsewhere.

### 3. The token (ExpeditionWindow3D)

`BuildFieldPartyToken`: three staggered figures, two packs, and a standard with
the school's colour burning at the top. Same `Prism` vocabulary as the castle
so the two read as one game, and deliberately NOT a scaled-down castle, because
at survey zoom the silhouette is all the player gets and a small castle reads
as a distant castle. One wide mass with stacks versus several narrow verticals
with a bright point.

No `OmniLight3D`. The castle carries the lantern because it has a furnace in
it; the standard is emissive instead, which reads at distance without lighting
the ground or doubling the moving light sources in the window.

### 4. Castle-only controls

- Road-follow hidden. "Take the road until the furnace runs dry" is a fortress
  order; an autopilot that walks a radius-6 party off the edge of their own
  window is a trap.
- The dry-budget dialog keeps its shape and changes its words: "Turn back" or
  "Push on" against Health, instead of "Make camp" or "Push on" against Hull.
  A field party cannot become a waypoint, because a waypoint is something the
  castle CONJURES.
- HUD reads `Rations` and `Health`, not `Fuel` and `Hull`. Same counters. A HUD
  that told a party on foot their fuel level would be the clearest possible
  signal that the field run is the castle run wearing a hat.

### 5. Send party (deploy drawer)

A third button beside Deploy and March, labelled with the dives remaining and
disabled at zero. It opens a roster picker, which is the piece that was
genuinely missing: `Companion.Posting` has been in the schema since increment 1
and NOTHING IN THE GAME COULD WRITE IT, so the field party was permanently
empty and the entire second force was inert.

Crew members appear in the picker and can be taken. That is the interesting
decision in the whole feature: every station a companion vacates costs the
fortress its crew effect on the next move. Hiding crew would have removed the
tradeoff and left two unrelated rosters. Injured companions are listed and
disabled rather than hidden, so "where is Elara" is answered on the screen that
asks the question.

Taking someone OFF the party returns them to Campus, not to Crew. Promoting
into a station silently would change the fortress's characteristics without the
player asking for it.

**A dive is not a lunation.** `Deploy()` forks: a field deploy spends
`ExpeditionTurn.DivesSpent`, does not advance the calendar and does not run the
world tick. Three dives burning a quarter of the cycle would have made the
split a downgrade. Field window radius is 6 (127 tiles) against the castle's 12
(469), because keeping map reveal on the castle is what stops three dives a
lunation from tearing the fog off the world and collapsing the 12-lunation
clock.

### 6. FOUND: the party dragged the castle behind it

`RecordCastleWorldPosition` is called from `OnPartyMoved`. On a field run every
step the PARTY took would have teleported the castle to follow them, and the
castle would have ended the dive standing wherever the party stopped, with its
anchor, march range, supply line and threat rolls all computed from the wrong
tile.

Guarded at the one point all five callers funnel through, not at the five call
sites, because the next person to add a sixth will not remember the rule.
`DockCastle` and `ParkCastle` are guarded too.

### 7. FOUND: five systems asked the wrong roster

`CompanionRoster.GetActiveParty`, `CompanionInjurySystem.ApplyWipe` and
`ApplyExtractionCheck`, `CompanionPerks.ExtractionGold` and
`LoyaltyEvents.OnExtraction` all resolved "who was on the expedition" from
`ActivePartyCompanionIds`. A field party that got wiped would have injured THE
CREW, and a fight during a dive would have spawned the people who stayed home,
because combat spawns and negotiation tokens both read the party through
`GetActiveParty`.

New `CompanionRoster.RunRosterIds(save)` is the single answer, and all five
call it. Gated on `ExpeditionRunKind`, which `StrategicView._Ready` resets to
Castle, so Field is an expedition-scene state and the campus never sees it.
Deliberately not also gated on `IsOnExpedition`: the extraction sequence clears
that flag before the casualty rolls run.

## Verification

Brace, paren and bracket deltas (0,0,0) against HEAD on all eight changed
files; FieldExpeditionState.cs balances as a new file. Em-dash greps clean
tree-wide, both forms. Every changed signature audited for call sites.

Still no compiler on this machine.

## Known gaps, named rather than hidden

- **Travel is not modelled.** The picker sends the party to a staging point and
  the dive happens there. `FieldParty.TravelPhasesRemaining` and
  `ExpeditionAnchors.TravelPhases` exist and are not yet spent: a dive is
  currently instant from anywhere. Travel is a separate increment.
- **The party does not carry the hold.** The ruling says a field party brings
  the castle's hold home; today only the resupply teleport banks it.
- **Built waypoints cannot be conjured.** `Waypoint` and `AnchorKind.Built`
  round-trip in the save and nothing creates one.
- **`FieldRations = 18` is reasoned, not measured.** Sized against a radius-6
  window (127 tiles) as "a loop to the rim and back with a margin". It has not
  been played. Expect to move it.

---

# Increment 3b: closing the four gaps

The four things increment 3 named as unfinished, fixed rather than documented.
Three more holes turned up on review, two of them in the fixes themselves.

## Gap 4 first: FieldRations, measured

Shipped at 18 on intuition, which the log said plainly. Now simulated: 4000
nearest-neighbour tours per cell over a radius-6 window (127 tiles), using the
real `TerrainStep` table and POI density read out of `ScatterPois` (settlement
footprints ~50% studded, wilderness thinned to `WildPoiPerKingdom = 5`), across
three land mixes with mean step costs of 1.55, 1.80 and 2.21. Every tour walks
BACK, because a field party extracts where it started.

Median cost to work k POIs and return:

| k | gentle | typical | harsh |
| --- | --- | --- | --- |
| 3 | 7.8 | 9.0 | 11.1 |
| 4 | 9.3 | 10.8 | 13.3 |
| 5 | 12.4 | 14.4 | 17.7 |
| 6 | 14.0 | 16.2 | 19.9 |

18 bought 5 to 6 POIs on typical ground at the median, which is essentially
everything within reach. That is the argument against it: **a budget that never
binds is decoration.** The "push on hungry" choice never fires, and all three
dives in a lunation play identically because the first one cleared the
neighbourhood.

**14.** Four to five POIs on typical ground, three to four on harsh, so the
player chooses which sites to work and the walk home is a real cost. The
simulation's uncertainty is the terrain mix, not the tour arithmetic.

## Gap 1: travel

`TravelPhasesRemaining` existed and nothing wrote it, so a dive could be
ordered from any anchor on the map and the field party was a teleporting drone.

The deploy drawer's field control now has three states, because the question
the player asks at an anchor is always the same one:

- **travelling** : disabled, says how many phases until they land
- **standing here** : "Send party (N)", spends a dive, plays the window
- **elsewhere** : "Travel here (Np)", spends time, plays nothing

`OrderTravel` sets the journey; `TickFieldTravel` runs in `RunLunationTick`
beside the castle's resupply, decrementing by `CalendarState.PhasesPerLunation`
(8). A journey shorter than a lunation lands in one turn of the moon; a long
one does not. That is the whole pacing decision and it is why `TravelPhases`
counts in phases rather than lunations.

Travel turns no moon by itself. The confirmation says so in as many words:
the castle's next move is what advances them. A player who orders a march and
then waits has been told the wrong thing, not handed a soft lock.

`EnsureFieldPartySited` seeds the party at the castle, or nothing is ever "here"
and the drawer offers travel to the tile they are standing on.

## Gap 2: the party carries the hold

`FieldParty.Carrying`, a `CastleHold` because it holds the same thing and a
second bundle type would mean two `Add` methods to keep in step.

Loaded when the party departs the castle's park, which is the only moment it
can happen: they are standing where the spoils are and about to go somewhere
with people in it. Banked on arrival at a **staging** anchor. A shard zone and
a built waypoint are not places with a waystone and a counting house, so
arriving at one carries the hold onward and says so.

## Gap 3: built waypoints

`ConjureWaypoint` / `CanConjureWaypoint` / `WaypointChargesFromModules`, and a
"Raise Waypoint" control on the castle's HUD (row five). Costs
`WaypointMaterialCost = 40` build materials, charged in materials rather than
fuel because the fortress is standing still while the work is done. Charges are
1 plus any installed module with the `waypoint_charges` effect: no module grants
it yet, and the hook is what makes waypoint depth an upgrade rather than a
constant.

Shown only on a charted POI and disabled with the refusal as its tooltip.
Hidden rather than greyed on ordinary ground, where the control is not refused,
it is irrelevant, and a permanently disabled button on 95% of tiles is
furniture.

## Three holes found on review

**A. The destination was stored only as a key.** Arrival resolved
`DestinationAnchorKey` against the live anchor list. The parked castle appears
there under a `castle:` key while its staging point is `staging:`, so a march to
the fortress resolved to null and **the party arrived without moving**. A
waypoint that spent its last charge mid-journey vanishes from the list entirely,
same result. Added `DestX`/`DestY`: a destination is a place, not a row in a
table that may have been rebuilt since. `FindAt(cycle, x, y)` added for the
same reason, because one tile can carry two anchor rows with different keys.

**B. A conjured waypoint was unclickable.** Waypoints went into
`cycle.Waypoints` and surfaced in `All()`, and **nothing else in the game reads
`All()`**. The deploy drawer, the map markers and the supply-anchor rules all
read `StagingPoints`. Raising a waystone did nothing at all. Fixed with the
trick `ParkCastleAt` already uses: give it a real `StagingPoint` (source
`"Waypoint"`) and it inherits the whole shipped machinery. `All()` skips that
source so it is not listed twice.

**C. Spending the last charge stranded the party.** A dive spends the charge at
DEPLOY. Removing the staging point there would have deleted the anchor the
party is standing on and then sent them out, so `OnSupplyAnchor` would find
nothing on their own tile and **every extraction from a last-charge waystone
would have been an emergency one**, with the straggle lunation and the
infirmary check that come with it. `PruneSpentWaypoints` now runs on strategic
load instead: the waystone holds until they are home.

## Touched

`ExpeditionAnchors.cs` (travel, hold transfer, waypoints, `FindAt`,
`PruneSpentWaypoints`), `FieldExpeditionState.cs` (`Carrying`, `DestX/DestY`),
`ExpeditionManager.cs` (rations, waypoint control), `StrategicView.cs` (drawer
states, `ConfirmFieldTravel`, travel tick, prune, charge spend),
`FieldExpeditionSaveAssert.cs` (round-trip for the three new fields, including
the nested `Carrying` object, which is exactly the shape that silently
deserializes to null and eats a cycle's spoils).

## Verification

Balance deltas (0,0,0) against HEAD on every pre-existing file; the two new
files balance. Em-dash greps clean tree-wide. Every new public method has a
definition and at least one caller (audited by name count).

Still no compiler here.

## What is genuinely still open

Nothing from the previous gap list. New and smaller:

- Travel has no interception. A party crossing hostile ground cannot be caught,
  which is a gift compared to what patrols do to the castle.
- The party cannot conjure or repair anything; waystones are castle-only.
- `WaypointMaterialCost = 40` is reasoned against a cycle's material income, not
  measured the way `FieldRations` now is.

---

# Increment 3c: POI ownership, and the road is watched

Two rulings (Magos, 2026-09-22): the castle uses up no POIs, the party is how a
POI gets used; and the party can be intercepted by patrols.

## The first ask was half done, and the half that was done was broken

`OnPartyArrived` already had a "the CASTLE does not work POIs" block, written in
an earlier increment when the user asked for the stride to stop interacting with
sites. It gated on `IsEncounterPoi(poiType)` and NOTHING ELSE, because it was
written before the field party existed.

So Combat, Narrative, Negotiation, Prison and Objective sites were unworkable by
**either** force. That content was unreachable in the shipped build, and it had
been for two increments. The whole fix is the `!_fieldRun` term.

## Where the line now sits

New `IsCastleLogisticsPoi`: the ONLY sites a castle may still work are
**Outpost, Settlement, Seat**. Everything else it charts and leaves.

The test is "does working this OPEN THE MAP, or is it SPENT FOR REWARD". An
outpost secured becomes a staging point; a settlement and a seat are contact
with a polity. Those are a fortress arriving somewhere, and they are how the
anchor network the field party travels on comes to exist at all. A rest site and
a supply cache are consumed for Essence, gold, splinters and materials, which is
reward, so they went to the party with the rest of the content.

**Consequence, stated rather than discovered in play: the castle now has NO
mid-sortie Essence recovery**, because Rest was the only source. That tightens
long sorties and pushes the fortress to park sooner, which is the direction the
redesign wants. If it plays badly the fix is one name in one predicate, which is
why the split is a predicate and not five scattered conditions.

## Interception, part one: already worked

Patrol ambush during a DIVE needed nothing. The ambush path is in the shared run
loop with no castle-specific gate, so a field party in a window has always been
interceptable. Verified by reading the gate rather than assuming it.

## Interception, part two: the road

`FieldThreats`, mirroring `CastleThreats`, rolled in `RunLunationTick`
IMMEDIATELY BEFORE `TickFieldTravel` so a party that is turned back does not also
step toward the place it was just turned back from.

- **PatrolIntercept**: takes 35% of what the party is CARRYING and costs 3
  phases. Common, survivable, and the reason hauling the hold across the map is
  a decision rather than a formality.
- **KingdomIntercept**: the party is turned back to where it set out from, the
  march is lost, and one companion is injured for 2 lunations.

Stakes are `Carrying`, travel phases and injury because those are the only three
things a TRAVELLING party has that persist in `CycleState`. It has no window, no
Hull and no ExpeditionHP while abstract, so inventing a stat to damage would
mean inventing a stat that exists only to be damaged.

**No tactical fight.** A fight needs a battlefield and an abstract journey has no
ground to fight on. The castle gets one because it is parked somewhere specific.
Giving the party one would mean picking a tile at random and pretending.

Risk is read at the MIDPOINT of the journey, not at either end: an interception
happens on the road, and the road's character is not that of the safe city they
left or the safe city they are heading for.

Escort reduces the odds, 6 points per companion past the first, capped at 24.
That makes the roster picker a decision about the ROAD and not only about the
dive at the far end. Capped because an interception the player can switch off by
over-staffing stops existing the moment it is understood.

## Tuning: measured before shipping, not after

First draft was base 15 / per-phase 4 / kingdom share 55. 20,000 simulated
journeys per cell said that was a wall:

- 12-phase march, neutral ground, solo: **74% intercepted**
- 12-phase march, hostile ground, solo: **70% turned back**

A road that cannot be used is not a decision. Per-phase 4 also dominated every
other term, so stance and escort stopped mattering and only distance did.

Shipped at base 10 / per-phase 2 / kingdom share 40:

| journey / ground / party | clear | robbed | turned back |
| --- | --- | --- | --- |
| short 3p neutral x1 | 83.8% | 16.2% | 0.0% |
| short 3p neutral x3 | 96.2% | 3.8% | 0.0% |
| short 3p hostile x1 | 59.3% | 24.5% | 16.2% |
| medium 6p neutral x1 | 78.2% | 21.8% | 0.0% |
| medium 6p hostile x1 | 53.0% | 24.6% | 22.3% |
| long 12p neutral x1 | 54.7% | 45.3% | 0.0% |
| long 12p neutral x3 | 73.6% | 26.4% | 0.0% |
| long 12p hostile x1 | 22.8% | 37.8% | 39.4% |
| long 12p hostile x3 | 36.5% | 33.6% | 29.9% |

Dropping the kingdom share from 55 to 40 is what turns hostile ground from
closed into expensive: most hostile interceptions are now robberies, so the
party gets through poorer instead of failing outright half the time.

The number most likely to be wrong is `PerPhaseChance`, because it is the one
that compounds. The table is in the file so the next person can see what the
current values actually produce.

Rolls are deterministic per lunation, seeded from world seed, lunation, party
tile, phases remaining and party id: a threat table you can reload away is not a
threat table.

The drawer tooltip and the march confirmation both say the road is watched
before the player commits, rather than after they are robbed.

## Verification

Balance deltas (0,0,0) against HEAD on both changed files; FieldThreats balances
as a new file. Em-dash greps clean tree-wide. `FieldThreats.RollForLunation` has
exactly one caller, in the right place in the tick order.

Still no compiler here.

---

# Increment 4: two pieces on the board

Ruled 2026-09-22 (Magos), and it is a correction to the design I had been
building, not an addition to it:

> The field party should have their own expedition marker that moves
> independently from the castle. You should be able to send commands to either
> and they move independently from one another. The castle serves for long range
> scouting and setting a mobile teleportation node for the field party to move
> to. The field party should be able to do everything the expedition token was
> able to do before the changes we started in this work.

## What I had built, and why it was wrong

I had made the field party an ABSTRACTION. It had no marker. Its position
between anchors was a phase counter nobody could watch. Its destinations were
restricted to rows in the anchor list. Its expedition was a cut-down radius-6
"dive" with a 14-ration budget.

That is a travel ACCOUNTING system, not a piece on a board. It made the party
something the player configured rather than something they moved, and it made
the castle the only thing on the map with agency. None of that follows from
"split the forces"; I inferred it and never checked.

Asked before rebuilding rather than guessing twice.

## What was undone

| Was | Now |
| --- | --- |
| Radius-6 dive window | Radius 12, the window the token always had |
| `FieldRations = 14` | 40, the pre-split `OperatingRange` |
| Abstract phase journey to anchors | The party walks the world map, marker and all |
| Deploy only from an anchor | Takes the field wherever it stands |
| Three-state field button on the deploy drawer | Gone; the party is not a thing that happens at staging points |
| `ExpeditionAnchors.OrderTravel` / `TickFieldTravel` / `TravelPhases` / `PartyIsAt` / `TilesPerTravelPhase` | Deleted, not left beside the real system |
| `StrategicView.ConfirmFieldTravel` | Deleted, superseded by `TryMovePartyTo` |

The radius-6 ration measurement is not wrong, it is VOID: it answered a question
about a window that no longer exists. Saying so is cheaper than pretending 14
was ever the right answer for 469 tiles.

## What was built

**Two markers** (`WorldAtlas3D.BuildPieceMarkers`, fed by `SetPieces`). A wide
low blue prism for the castle, a narrow tall gold one for the party, each with a
label, the selected one wearing a ring. Drawn LAST in `RebuildMarkers` so pieces
sit over places. Before this the castle was represented only by the gold staging
beacon it drops when it parks, which is a place and not a piece, and the party
had coordinates in the save that nothing drew.

A march in progress draws its route as a line of gold marks to the destination,
so a walking party is visible on the map rather than only in a report.

**One selector, one move verb.** `_pieceToggleBtn` flips which piece takes
orders; the existing March control becomes "March castle" or "Move party" and
the armed click routes accordingly. A toggle rather than two buttons because the
pieces are exclusive, and switching pieces DISARMS the verb: an armed order
belongs to the piece it was armed for, and silently repointing it is how a
player marches the fortress somewhere they meant to send three people on foot.

**`FieldMarch`**, deliberately not a copy of `CastleMarch`:

- The castle relocates INSTANTLY, pays fuel, and owes an exposed camp. It is a
  machine that packs up and redeploys.
- The party WALKS, `TilesPerLunation = 8`, advancing each turn of the moon,
  charting radius 1 behind it (against the castle's 2, so the fortress stays the
  better scout), and it can be intercepted on the road.

Neither is the other with different numbers.

**Teleport**, and it is deliberately narrow: the parked castle, a raised
waystone, or the guild dock. Nothing else. If every anchor were a jump target
the map would have no distance in it and the castle's node would be one
convenience among many rather than the reason to move the fortress. Instant and
free, because the cost was paid marching the castle there or raising the
waystone.

One click, two verbs: a waystone tile steps them through, anything else is a
march. The tile decides, not a second mode, because "go there" is one intention.

**Take the field** (`OnPartyTakeTheField`) synthesises a staging point on the
party's own tile and runs the shipped deploy path, exactly as the warfront
intervention does. The expedition scene has always been driven by a coordinate
and a radius and has never cared whether a real launch point sits under it. This
is the part that makes the party a piece: it does not need permission from the
anchor list to act.

Marching into unseen ground is refused with "Nobody has charted that ground.
Send the castle to scout it first", which is the castle's new job stated as the
refusal, at the moment the player runs into it.

## One thing found while wiring

`RefreshPieceChrome` is called from `BuildAtlas3D`, which runs BEFORE the chrome
buttons are constructed, so that first call could only ever set the map and left
every control null. Called again at the end of the chrome build. Without it the
selector opened reading "Castle" by luck rather than by state and "Take the
field" never appeared at all.

## Verification

Balance deltas (0,0,0) against HEAD on every pre-existing file; the new files
balance. Em-dash greps clean tree-wide. Grepped for residue of every deleted
symbol: zero references to `TravelPhases(`, `PartyIsAt`, `TilesPerTravelPhase`,
`OrderTravel`, `TickFieldTravel`, `ConfirmFieldTravel`, `FieldWindowRadius`.
Every new public method has at least one caller.

Still no compiler here, and this increment touched the strategic map's chrome,
its click routing and its marker rebuild, so it wants a run before anything
else is stacked on it.

## Carried forward, unchanged by this

`FieldThreats` still reads `FieldParty.TravelPhasesRemaining`, which now counts
TILES rather than phases. The field is the generic "distance still owed"
counter and the per-unit risk means the same thing either way: a longer road is
a riskier one. The measured interception table was built against phases, so the
rates it predicts are now reached at different distances. That table needs
re-running against tiles before it can be trusted again, and it is flagged here
rather than quietly left to drift.

---

# Increment 4a: a tooling flaw ate a method

`CS0103: The name 'OpenFieldPartyPicker' does not exist in the current context`,
StrategicView.cs(4288).

## What happened

Removing the abstract-travel system meant deleting two whole methods, which is
not something exact-match replacement does well, so I wrote a `cut(path, start,
end)` helper that deletes everything between two text markers.

Its first call was start = `ConfirmFieldTravel`'s doc comment, end =
`OnPartyTakeTheField`'s doc comment. The file order was:

    ConfirmFieldTravel
    OpenFieldPartyPicker      <- sat between the two markers
    OnPartyTakeTheField

So it deleted `OpenFieldPartyPicker` as well, and the call in
`OnPartyTakeTheField` survived to name the corpse.

## Why this is the same mistake twice

Recorded on 2026-09-21: "my patch helper accumulated edits and wrote once at
the end; a late assertion discarded 5 edits that had printed OK". The fix then
was to write after every edit, and the lesson I drew was about WRITE TIMING.
That was the narrow reading.

The real lesson is that **every patch operation must assert what it is about to
change**, and `cut` asserted only that its two markers exist. It never checked
what lay between them, which is precisely the thing it deletes. A helper that
cannot describe its own blast radius has no business being pointed at a
4,800-line file.

Exact-match replacement has this property for free: the text I am deleting is
the text I typed, so I cannot delete something I did not look at.

## Fix

`OpenFieldPartyPicker` rewritten and reinserted, updated for the current model
(it now receives a synthesised staging point on the party's own tile and sets
`_pendingRunKind` before calling `Deploy`). Nothing else in it changed.

## The sweep that should have followed the cut, run now

Rather than fix the one name the compiler happened to reach first, extracted
every method DEFINED in both files the cuts touched and cross-checked it against
every identifier CALLED anywhere in `Scripts/`. Thirty symbols across the whole
Expedition v2 surface, including everything either cut sat near:

    missing definitions: 0

`ConfirmFieldTravel` reads 0 defined / 0 called, which is the intended deletion
rather than a second casualty. `ExpeditionAnchors` came through its cut intact:
`OrderTravel` and `TickFieldTravel` were genuinely the only things between those
two markers.

Balance deltas (0,0,0) against HEAD on both pre-existing files; the three new
files balance. Em-dash greps clean tree-wide.

## Standing rule, added

No range-based deletion in a live file. Deleting a method means pasting its
exact text as the `old` side of a replacement, however long it is. If that is
too tedious to do carefully, that is a signal the edit is too large to be doing
blind, not a reason to reach for a blunter tool.

---

# Increment 5: the FORCES roster

Asked for by name, 2026-09-23: "a selector on this screen that allow you to
select and give commands to any party or castle you control in the world".

## Why the thing I shipped was not that

Two faults, and the second is the one that matters.

**It was invisible when it mattered.** The piece selector was a two-state button
in the RIGHT-docked chrome stack. The deploy drawer covers that stack
completely. So the control for choosing which force to command could not be seen
at the exact moment the player was looking at a drawer thinking about where to
send a force. Visible in the screenshot: the whole right column is drawer.

**It could not say "any party".** `_partySelected` was a bool. `MaxFieldParties`
is a campus upgrade and the turn loop has iterated the party list since the
schema landed, so a boolean was always going to have to become an id. It has:
`_selectedPieceId`, empty for the castle, otherwise a `FieldParty.Id`.

## The roster

Left side, under the lens row, which is the one band of chrome nothing else
docks into and the drawer cannot reach.

    FORCES
    ▪ Castle        (66,48)  ·  fuel 40/40
    ▲ Field Party   (66,48)  ·  3 afoot  ·  carrying spoils

One row per force, name over a status line, accent bar down the left of the
selected one matching the ring that piece wears on the map, so the panel and the
board agree without the player having to check.

The status line is not decoration. "Can I give this an order right now" is the
question the panel exists to answer, and a name alone does not answer it, so
each line LEADS WITH WHATEVER BLOCKS AN ORDER: under attack, then resupply
lunations, then out on a sortie, then fuel. A marching party reports tiles
remaining rather than a state word.

Rows are rebuilt on every refresh; the panel shell is not, so it does not
flicker. Hidden in city view, like the March control, because these are
world-map verbs.

## The map caught up

`SetPieces` took a single party tile and a bool. It now takes LISTS of tiles,
names and destinations plus a selected index, so a second party needs no second
set of fields. Each party draws its own marker, its own name and its own march
route.

**Label stacking.** The screenshot shows "Castle", "Party" and the settlement's
own "edce (your seat)" printed on top of one another, because the party is sited
WITH the castle at the top of a cycle, so the first thing a new player sees is a
smear. Piece labels now step up by `PieceLabelStep` per label already on that
tile. Stacking the labels rather than the pieces, because the pieces have to
stay on their real hex. Keyed by tile, so a lone piece keeps its natural height
and only collisions pay.

## Orders follow the selection

`TryMovePartyTo` and `OnPartyTakeTheField` read `SelectedParty()` instead of
`EnsurePrimaryParty`. With one party those are the same object; with two,
reading the primary would send the wrong people. A stale selection (a party gone
between cycles) falls back to the castle rather than commanding a force that is
not there.

## The ordering bug I reintroduced

`BuildForcesPanel` went into `BuildLensButtons`, which runs LATE in `BuildHud`,
after the `RefreshPieceChrome` that would have populated it. The roster would
have opened empty and only appeared once something else moved a piece.

This is the identical bug fixed one day earlier for the chrome buttons, caused
again by putting a builder in a method that runs later than the refresh. The
band-aid is another refresh call at the new end. The fix, taken instead, is a
builder that does not depend on when it is called: `BuildForcesPanel` now ends
by calling `RebuildForcesPanel`.

## Verification

Balance deltas (0,0,0) against HEAD on both files. Em-dash greps clean
tree-wide. Residue sweep for every retired symbol (`PartySelected`,
`_partySelected`, `_pieceToggleBtn`, `TogglePiece`, `PartyTile`,
`PartyDestTile`): clean. Every new method has a definition and a caller.

## Note on where this was built

This session is now linked to the Mac (`~/Development/Fractured_Arcana`), not
the Windows box the earlier increments were written on. The Mac has commit
054a1b3 "Seperated party and castle navigation" with a clean tree, so the two
machines agree; these edits are on top of that commit.

---

# Increment 5a: the roster was built in city mode and never told the city closed

Reported: the FORCES panel did not appear. Two readings of the code did not
explain it, so a one-shot probe was added instead of a third guess. It printed:

    [Forces] rows=0 visible=False inTree=True pos=(16, 118) size=(268, 260)
             minSize=(268, 20) parent=StrategicHud selected=''

Every value is diagnostic. The panel EXISTED, was in the tree, under the right
parent, at the right position, at the right size. It had no rows and was
invisible. That rules out everything I had been theorising about (layout,
minimum size, CanvasLayer ordering) and points at exactly one line.

## The cause

`RebuildForcesPanel` read `_atlas3D.CityMode`, set `Visible = !cityNow`, and
returned early when true. The strategic map OPENS IN THE HOME CITY, so at
`BuildHud` time that read was TRUE: the panel was built hidden and empty, and
nothing ever revisited the decision.

`OnCityModeChanged` is the handler that exists precisely to revisit it. It
updates `_cityLeaveBtn`, `_annexButton`, `_buildModeBtn` and `_cityServicesBtn`
on that signal, and simply did not know about anything added since.

## The same bug had been hiding the March control since the day it was added

`_marchModeBtn.Visible = !cityNow` was set once at build, from the same read,
and `OnCityModeChanged` did not touch it either. So the armed-move verb has been
invisible for its entire life, which is why the castle March was only ever
reachable from the deploy drawer, and why "there is no visible way to march the
castle" was reported once, answered with a new control, and the new control was
never seen either.

Found only because the roster failed the same way and got a probe.

## Fix

- `OnCityModeChanged` calls `RefreshPieceChrome()`.
- `RefreshPieceChrome` OWNS world-map-verb visibility: it sets
  `_marchModeBtn.Visible` (and disarms the mode on entering a city) rather than
  the build site doing it once. The build-time assignment is deleted, not
  merely supplemented, so there is one writer.
- `RebuildForcesPanel` builds its rows whether or not it is visible. The early
  return was what left the panel with nothing to show once it WAS shown.
- Probe removed.

## The general shape, worth keeping

A control whose visibility is computed once at construction from a mode that
changes is a control that will be wrong for most of the session. The codebase
already had the correct pattern one method away: a signal handler that
re-decides. Three controls were added over two days without being added to it.
Anything gated on `CityMode` belongs in `OnCityModeChanged`, and the way to keep
that true is for exactly one method to own each control's visibility.

## Verification

Balance delta (0,0,0) against HEAD. Em dashes clean. `cityNow` at build time now
appears only where `OnCityModeChanged` maintains the matching control.

---

# Increment 6: let the moon turn

Asked for: "options on the strategic screen to assign all moves before
deploying for the phase". Built the narrow version, deliberately.

## The defect underneath the request

The world tick ran ONLY inside `Deploy`. So every slow process in the game was
hostage to the castle sortieing:

- a field party ordered to march took no step until the fortress deployed
- the resupply clock, the castle threat rolls and the road interception rolls
  were all gated the same way
- nothing on screen said a lunation boundary was coming or what it would resolve

Ordering a march and then finding that nothing happens because the OTHER force
has not acted is not a decision, it is a puzzle about the implementation.

## Why not a full commit-all-orders phase

That was the other option and I argued against it. Locking every order before
anything resolves costs the dive-then-decide loop: today you learn something in
the field and THEN choose where the fortress goes. Simultaneous-order games
usually give the player full information; this one deliberately does not, and
removing "act on what you just found" from a game about fog is a real loss.
Agreed with the narrow version.

## Built

**`AdvanceMoon(cycle)`**, extracted from `Deploy`. Two callers now share one
implementation, so a castle sortie and a deliberate wait cannot resolve the
world differently. Returns false when the cycle ended so the caller stops.

**"Let the moon turn"**, docked to the FOOT OF THE ROSTER rather than the
chrome stack. "Have I given everyone their orders" and "end the lunation" are
the same thought; putting them on opposite sides of the screen is how a player
turns the moon with a force standing idle.

The confirmation names what will actually resolve (resupply dropping to N,
which party walks how far and whether it arrives, the world moving) and, when
anything is idle, names it: "Nothing is ordered for: the castle, Field Party.
Their lunation passes unused."

Deliberately NOT free: it spends a lunation of twelve and runs corruption,
kingdom drift, sieges and every threat roll. It is the waiting move, and
waiting costs the same clock everything else does.

**Roster order lines.** Each force's status now ends in its standing order: a
marching party reads `(66,48) -> (71,52) · 6 tile(s) to go`, an unordered force
reads `· awaiting orders`. Blank would have been quieter and worse: "nothing is
assigned here" is exactly what the player needs to notice.

## Verification

Balance delta (0,0,0) against HEAD. Em dashes clean. `crossedLunation` now
appears only inside `AdvanceMoon`, so the inline copy in `Deploy` is gone
rather than duplicated.

---

# Increment 7: the scrying table commands either force

Ruled 2026-09-23 (Magos). The expedition view IS a scrying projection: a table,
a chamber, a glowing disc of the world. Aiming it at a different force is how
one wizard commands two forces in different places.

## What this actually required

A run had to survive not being watched. Until now the whole of a run lived in
ExpeditionManager's fields and existed only while that scene did.

The seam already existed and had been load-bearing since the beginning: the
COMBAT ROUND-TRIP freezes a run into EncounterRouter, changes scene, and thaws
it in RestoreFromCombat. Aiming the table is the same event with a different
reason for leaving, so it takes the same road rather than a parallel one.

`SortieState` therefore stores exactly what the combat round-trip stores and
nothing more. Everything it omits (fog, discovery, consumed POIs, waypoints,
the hold) lives in the world and is already durable. One slot on
`CycleState.CastleSortie`, one per `FieldParty.Sortie`; additive fields with
safe defaults, so a pre-feature save reads "not in the field", which is true.

## Why a scene reload and not an in-place swap

The nicer architecture is two `ForceRuntime` objects in one scene with the
window streaming between them, and `WorldWindowBuilder.StreamTo` already
supports the streaming. It was rejected: about forty fields in a seven thousand
line node describe ONE force, and turning them into a swappable struct is the
kind of refactor that breaks things nobody was looking at. This file has
already produced one regression, one deleted method and one reintroduced
ordering bug this week.

The reload costs a rebuild of a 469-tile window. The combat path pays it
several times a sortie and nobody has complained.

## Rules

- The table lists every force with its state.
- A force IN THE FIELD can be taken command of directly.
- A force that is not can be SENT, if sending costs no turn of the moon. A
  field party spends one of the lunation's expeditions, which is free of the
  calendar. The fortress sortieing TURNS THE MOON, so it belongs on the
  strategic map, and the row says so rather than letting the player find out by
  clicking a button that then does something enormous.
- Aiming away mid-march is refused: halt first. Freezing a run halfway through
  a step is a snapshot of a state the game cannot resume.
- The control hides once the run is over, like every other order control.

## The three-way fork in _Ready

    combat return   -> RestoreFromCombat   (existing)
    table return    -> RestoreSortie       (new)
    anything else   -> fresh deploy        (existing)

The middle branch takes the same SHAPE as the first for the same reasons: this
is not a new expedition, so carried HP must not reset, the run journal must
keep appending rather than opening a second file for one run, and the weather
must not be re-rolled underneath a force that never left.

`PlayerSession.ExpeditionTableSwitch` is a separate flag from
`HasPendingReturn` rather than a reuse of it, because the two can be true for
different forces at the same time: returning from a fight with the party while
the castle sits frozen mid-sortie is an ordinary Tuesday under this design.
`SwitchToForce` explicitly clears `HasPendingReturn`, or the force being
resumed would have the other one's combat numbers restored over the top of it.

## Slot lifecycle

Opened on any fresh deploy (at deploy, not at the first move, so aiming away on
the first frame still freezes a real run). Cleared on all FOUR ending paths,
found by their shared `PlayerSession.IsOnExpedition = false` line rather than
by hunting them individually. A slot left Active after a force came home would
let the table take command of somebody standing in the dock.

## Verification

Balance deltas (0,0,0) against HEAD on all five changed files. Em dashes clean
tree-wide. Every new symbol has a definition and at least one caller.
`FieldExpeditionSaveAssert` gained round-trip coverage for `SortieState`,
including Active, position, staging, window radius, fuel, hull and earnings: a
force the table is not watching exists ONLY as that object, and losing it in
serialization loses a sortie mid-flight with everything it had earned.

## Not done, and worth knowing

- The FORCES roster on the strategic map does not yet say "in the field" for a
  force with an active sortie. It will read "awaiting orders", which is wrong
  for a force that is out.
- Returning to the strategic map from one force while another is frozen
  mid-sortie is untested. The slot persists correctly; what the strategic map
  offers for that force is not yet defined.
- Turning the moon while a force is frozen mid-sortie is likewise undefined.

---

# Increment 8: the palantir

Ruled 2026-09-23 (Magos): swap between any number of forces WITHOUT LEAVING
THE SCENE. If loading is an issue, fog rolls in over the map and thins to show
the new ground. The vibe: the player stares into a palantir, scries a force,
gives it orders.

## The road not taken, and why the ruling was still met

The literal reading is an in-place swap: two or more force runtimes inside one
scene, the window streaming between them. `WorldWindowBuilder.StreamTo`
already supports the streaming. It was rejected AGAIN, for the reason given in
increment 7: about forty fields in a seven thousand line node describe one
force, and making them swappable is the refactor most likely to break things
nobody is looking at, in the file that has produced a regression, a deleted
method and a reintroduced ordering bug this week.

What the ruling actually demands is that the PLAYER never sees a scene change.
The fog was offered as the fallback, and the fog turns out to be the whole
answer: a scene reload hidden under fog that survives the reload is
indistinguishable, from the chair, from an in-place swap. It is also the
thing the fiction wants. A palantir does not cut; it clouds.

## ScryVeil, an autoload

It has to outlive the thing it hides. The expedition scene is torn down and
rebuilt during a switch, and anything parented inside it would vanish with it
and expose the rebuild. `HudManager` already proves a persistent layer
survives; the veil sits one layer beneath it (89 under 90), so the world bar
stays readable while the vision clouds. The frame holds, the stone fogs.

One full-screen `ColorRect`, one shader, one uniform (`coverage`) that a Tween
walks 0 to 1 to 0. The fog front advances from the rim toward the centre with a
noise-broken edge, the body drifts on a second slower noise, and a faint
violet bloom rides just behind the front: the stone working. At full coverage
the body is forced solid, because the rebuild underneath must not show through
an eight percent gap. Same grey as the window's Fog weather, so the two read as
one substance.

The rect is `MouseFilter.Ignore` while clear and `Stop` while down, so it never
costs a click on an ordinary frame and a click aimed at the old map cannot land
on the new one.

## Sequence

    aim the table ->
      CaptureSortie
      await RollIn        (fog fully down; player can do nothing)
      HoldOpaque          (snap, so the swap frame cannot peek)
      set the handoff, clear HasPendingReturn
      ChangeSceneToFile   (invisible)
    new ExpeditionManager._Ready ->
      sees IsCovering, defers LiftVeilDeferred one frame
      builds the run, restores the sortie
    frame + 1 ->
      Dissipate           (fog thins from the centre out over the new ground)

`RollIn` is awaited, so the scene only changes once the stone is blind.
`Dissipate` is deferred a frame past `_Ready`, so the fog only thins once every
node `_Ready` created has been through its own `_Ready` and drawn once.
Lifting it inline would thin the fog over a window that has not built yet,
which is the exact thing the veil exists to hide.

## Watchdog

`RollIn` arms a six second timer. If the scene that comes up never calls
`Dissipate` (a crash, a route that was not the expedition), the veil lifts
itself with an error in the log. A stuck veil is an annoyance; a stuck veil
with no control to clear it is a soft lock.

## Two things fixed on the way

**Any entry into a force in the field now resumes it.** The three-way fork in
`_Ready` was gated on `_tableSwitch`. Un-gated: a force whose sortie is Active
is in the field however this scene was reached, through the stone, back from
the strategic map, or after a crash mid-sortie, and starting it fresh would
overwrite a live run with full fuel and zero earnings. That is the one outcome
worse than any of the ways of getting here.

**The FORCES roster says "in the field".** With its remaining fuel or rations.
It had read "awaiting orders" for a force that was out, which was flagged in
increment 7 and is now wrong nowhere.

## Verification

Balance deltas (0,0,0) against HEAD on every pre-existing file;
`ScryVeil.cs` balances as a new file. Em dashes clean tree-wide including
`project.godot`. `ScryVeil` has six call sites outside its own file. The
autoload is registered. `async void` on a signal-driven handler has precedent
in `CombatManager`.

## Still open

Turning the moon while a force is frozen mid-sortie remains undefined. The
slot survives it correctly; what the world tick should DO to a force that is
out (interception, weather, resupply) is a ruling, not a bug.

---

# Increment 9: looking up from the stone

Ruled 2026-09-23 (Magos): the player can look up from the palantir, and
mid-expedition news arrives by one of three roads: a note on the desk beside
the stone, a messenger through the door, or a mind-to-mind sending that pauses
the scrying.

## The three roads, and why they weigh differently

| Road | Arrives | Interrupts | Waits |
| --- | --- | --- | --- |
| Note | a leaf on the desk | never | until the wizard looks up |
| Messenger | a figure through the door | announces, does not stop | until dismissed |
| Sending | through the stone itself | the stone flares, orders refused for 1.2 s | no |

The weight is the design. A note is for things worth knowing and never worth
stopping for. A messenger is someone in the room, so the scrying carries on
around them. A sending is the one road that comes from far enough away to earn
an interruption, and it is the only one that stops the player.

## The room

The rig already built a chamber, a table and a ring of stand-in figures as set
dressing. It now has a DESK on the camera's side of the table, where a desk
beside you would be, and a DOOR across the room. Notes are laid on the desk as
fanned pale leaves (up to six shown; the count is the truth). A messenger is a
stand-in figure spawned at the door and tweened to the wizard's side; dismissed,
they walk back out. A sending pulses the stone's glow light up threefold and
back.

## Looking up

`_lookUp` is a 0..1 blend INSIDE `PlaceCamera`, not a second camera. Pitch,
distance and target all lerp toward a room pose (16 degrees, 2.6 radii back,
focus above the table on the door's side), so the desk falls into the bottom of
the frame and the door into the middle, and Q/E still orbit at either end.
Orders are refused while looking up: a tile click with the map at the bottom
of the frame is a misclick waiting to happen.

The desk panel lists what is waiting, unread first, newest first, and opens any
of it. Read messages stay listed this cycle in case the wizard wants them
again.

## Storage first, presentation second

Every message goes into `CycleState.ScryInbox` BEFORE it is shown. A note
survives aiming the table elsewhere, a save, and a crash; `SeedDeskFromInbox`
re-lays what was waiting whenever the 3D window comes up. A note on a desk
does not vanish because the wizard looked away.

## Producers wired

- **Note**: weather closing over the force (with what it costs per tile); a
  waystone raised.
- **Messenger**: the shard guardian falling.
- **Sending**: the campus reaching through the stone when the Conjunction is
  within two moons, once per fresh run start. It is the one message that
  genuinely comes from far away, and the ONLY producer the channel has until
  the world tick can run while a force is in the field: the sendings that
  matter most ("the castle is under assault while you drive the party") need
  that ruling first.
- **Debug**: F6 / F7 / F8 post a sample on each road, so all three can be seen
  on demand rather than waiting for the weather to turn.

## Shape

`ExpeditionManager.ScryDesk.cs` is a PARTIAL of the manager rather than more
lines in it, so the desk reads as one thing and the seven thousand line node
does not grow. It shares the class and so every private field.

## Verification

Balance deltas (0,0,0) against HEAD on every pre-existing file; the partial
balances as a new file. Em dashes clean tree-wide. Every new symbol has a
definition and a caller. `ExpeditionWindow3D` is `partial`, which the new
`[Signal]` requires.

---

# Increment 9a: the switch worked and every run started at zero

From the play log, immediately after each fresh deploy:

    [Sortie] Resumed castle: at (-1,-1) 0/0 0/0
    ...
    [Sortie] Frozen castle:  at (77,52) 0/40 0/20
    [Sortie] Frozen field_1: at (65,46) 0/40 0/20

Both forces at ZERO fuel and ZERO Hull. The table switch itself was working,
which is what made this look fine.

## Cause

`openSlot.Active = true` (the "this force is now in the field" mark) sat ABOVE
the three-way fork in `_Ready`. So on a fresh deploy the slot was marked Active
while still empty, the fork read Active as "already in the field", took the
resume branch, and `RestoreSortie` wrote a slot holding nothing over the
freshly computed tank and hull.

It only looked like it worked because nobody had tried to take a step.

## Fix

The slot is opened INSIDE the fresh-deploy branch, after the fork has decided
this is a fresh deploy. One writer, on the one branch where the statement is
true.

`SlotIsResumable(slot)` now decides the fork: Active is necessary and not
sufficient, and a slot with no position or no tank is not a run. It clears the
slot and returns false, so the fork falls through to the fresh branch.

## A second mistake caught before it shipped

The first draft of that guard was an early return INSIDE `RestoreSortie`. That
would have returned from the resume branch without ever reaching the fresh
branch, leaving the party token never placed and the run journal never opened.
The check has to live in the fork's CONDITION, where a "no" can still choose
the other branch. Moved before it was built.

## The exit error

    NullReferenceException at GCHandleBridge.GCHandleIsTargetCollectible

Fired at "Debugging process stopped", not during play. That is Godot's C# glue
at shutdown finding a managed object, referenced by a native signal connection,
already gone: a delegate outliving its target. Increments 8 and 9 added several
lambdas subscribed to Tween and SceneTreeTimer signals, so one of them is the
likeliest owner, but the log does not say which and it fired only at exit.
Flagged rather than chased: if it appears mid-session it becomes a bug with a
reproduction, and that is when it is worth the time.

## Not mine, but in the way

`[ImbuementField] cleared ...` prints several hundred lines per scene load,
and every table switch is a scene load. The five lines that mattered in this
log were buried under about six hundred that did not. Worth gating on
DebugMode or removing, purely so the next log can be read.

---

# Increment 10: nothing dies off-screen

The ruling deferred since increment 7: what the world does to a force left IN
THE FIELD while the table looks elsewhere. Decided and built.

## The gap it closes

A frozen sortie was IMMUNE. `CastleThreats` gates on the castle being parked,
`FieldThreats` on the party travelling, and a force that is neither slipped
through both. Leaving the castle in the field was strictly safer than parking
it, which inverts the entire point of parking, and it was the reason the
Sending channel had one producer.

## The rule above the rules

**Nothing dies off-screen.** A frozen force is never reduced below one Hull,
one Health, or zero fuel, and is never wiped. The player comes back to a
damaged force, never to a funeral they did not watch.

This is the counterargument answered before it is raised: the table
ENCOURAGES leaving a force out while driving another, and a penalty harsh
enough to stop that would make the table decoration. So every consequence
here is mild alone and honest together, and none of them is terminal.

## Three consequences, per lunation, per frozen force

- **Upkeep**, always: the castle burns 3 fuel holding station, a party eats 4
  rations. A month camped is not free. Desk news, not a sending.
- **Raid**: a patrol finds a force that is not moving. 30% of what the SORTIE
  has earned (never the banked treasury) and 15% of maximum Hull or Health.
  The chance climbs 8 points per lunation already sat there, because the
  locals notice. `LunationsFrozen` counts moons UNWATCHED and resets on
  resume, so a force the wizard checks on every lunation is never "sitting
  long enough to be noticed".
- **Assault**, castle only, unfriendly or worse ground: soldiers. 30% of
  maximum Hull as the opening blow, and `PendingCastleAssaultKingdomId` is
  set, which hands the fight to the tactical castle defence that already
  exists for a parked fortress. Never while one is already owed.

## Measured, and the measurement corrected the design

The first draft of this file carried a "measured behaviour" comment I wrote
BEFORE running anything. The simulation happened to land within a point of it
on the neutral row, which is luck, and it caught something the guess missed:
a five-strong crew in friendly country rolled **zero percent for three straight
lunations**. A force the locals cannot touch is a force with no reason to ever
come home. `MinChancePercent = 3` now floors every roll.

P(hit at least once over N lunations), 20,000 trials, before the floor:

| ground / defenders | N=1 | N=2 | N=3 | N=4 |
| --- | --- | --- | --- | --- |
| friendly x1 | 2% | 12% | 27% | 47% |
| friendly x5 | 0% | 0% | 0% | 6% |
| neutral x1 | 12% | 29% | 50% | 68% |
| neutral x3 | 2% | 11% | 28% | 47% |
| hostile x1 | 35% | 62% | 82% | 92% |
| hostile x5 | 14% | 33% | 53% | 71% |

The shape: one lunation away is cheap, three is a gamble, hostile ground is
where you do not leave things.

## How the news reaches the wizard

Every raid and assault is written as a SENDING into `ScryInbox`. The next time
an expedition comes up, `DeliverPendingSendings` plays every unread sending in
turn: the stone flares, orders are refused for a beat, the words arrive, and
the next one waits for that to be dismissed. Sequential on purpose. Three raids
delivered as three stacked dialogs read as a bug.

This is the traffic the road was built for in increment 9: turn the moon on the
map, aim the stone at the party, and it clouds with "The castle was raided
while you looked away."

Upkeep alone posts a Note, not a Sending. Nobody reaches through the stone to
say the furnace is banked.

## Verification

Balance deltas (0,0,0) on every pre-existing file; `SortieThreats.cs`
balances. Em dashes clean tree-wide. `LunationsFrozen` has round-trip
coverage in the save assert. One caller for the roll, in the tick, before the
road interception and after the parked castle.

## Flagged

The assault path sets `PendingCastleAssaultKingdomId` for a castle that is in
the field rather than parked. The defence round-trip that consumes it was
written for a parked castle; it reads `CastleX/Y`, which a mid-sortie castle
keeps current, so the fight lands on the right tile, but the repair setback
it applies on withdrawal means nothing to a castle with no camp. Harmless and
slightly wrong. Worth a look when the defence is next touched.

## Increment 10a: the poisoned slot, and a room worth looking up at

**"Spawning with no fuel" was not a fresh-deploy bug.** The fork fix in increment 9 was correct; the screenshot (0/40 rations, 0/20 Health, quarter-Hull halt card before a step) is a RESTORE. Under the old ordering bug a run resumed an empty slot and ran at 0/0; aiming the table away then captured that 0/0 with a real position and the recomputed tank, so the saved slot passed `SlotIsResumable` on the fixed build. `SlotIsResumable` now also refuses `CurrentHP <= 0` (nothing legitimate produces it: raids floor at 1, ending paths clear the slot) and clears it, so the fresh branch redeploys. Old saves self-heal on next entry.

**The chamber.** Was a floor disc in the void with a 4 unit desk beside a 24 unit table. Now sized off R: wall at 2.8 R (look-up camera sits at about 2.05 R), wall height 1.3 R, ceiling, 8 pillars half a step off the cardinals, 8 warm sconces on the cardinals (one over the door), door set into the far wall with a frame. Figures 6.0 tall (was 3.2) with girth following height; desk 7.4 x 3.0 x 4.2; note leaves scale with the desk. `ChamberFogDensity` 0.032 to 0.016: at 0.032 the far wall kept 4% of its colour and no lighting could have shown it. `DoorDistance` export removed (door lives on the wall). All new sizes are exports under "Scrying Chamber Room".

## Increment 10b: the starting tile

**Screen-space label stacking.** Map labels keep a constant on-screen size (PixelSize is retuned per zoom), but their stacking offsets were fixed WORLD lifts (1.5 units), which shrink to under a line at whole-world zoom. Castle, Field Party and the seat's own name all drew on one spot on every new save. `WorldAtlas3D` now keeps `_labelStack` per tile: the first label on a tile takes its natural anchor, each later one climbs `screenFrac * 1.35` of screen height along the camera's up vector (`ApplyLabelScale` re-derives the position each camera move). Settlement labels seed the stack; piece labels join it.

**Bodies fan.** Co-located piece bodies (and the selection ring) are drawn offset from the tile centre: castle at the centre, parties around it at radius 0.95.

**`SortieState.IsLive()`.** One definition of "a run that can be resumed" (Active, real position, tank, HP > 0), read by the strategic roster, the frozen-sortie roll and the scry picker. `ExpeditionAnchors.PruneDeadSorties` clears Active-but-dead slots on the way into the strategic map, so a poisoned save stops reporting "in the field, 0/0 fuel" for a castle standing in the dock.

## Increment 10c: ground definition in the window

**Diagnosis.** "Play-dough pastel lump" is three absences, none of them texture: no relief for the light to catch (the C1 field plus HeightScale 0.65 turns a mountain into a 0.78 unit bump), a fill light strong enough (0.62 ambient vs 1.0 sun at 45 degrees) that every slope lands in the same toon band, and no edge structure of any kind (the lattice-artifact hunt removed every line, intended or not). A texture on a smooth lump is a textured lump.

**Levers, all exports on ExpeditionWindow3D:**
- Ground Definition: `RidgeAmp 0.55` / `RidgeDetailAmp 0.16` (ridged noise on 27 and 63 degree domains, wavelengths 1.8 and 0.9 units, squared crests, gated by a per-terrain `Ruggedness` blended through the wide height kernel: Mountain 1.0, Volcanic 0.85, Snow 0.6, Hills 0.45, plains 0.05, wet ground and water 0); `SlopeDarken 0.35`; `ContourInk 0.22` every `ContourSpacing 0.30` units (fwidth-antialiased, fades where lines would pack under 3 px and past 22-40 view units); toon bands 4 / softness 0.12 / jitter 0.6 (jitter was 0 in the window).
- Chamber Sun: pitch -34 (was -45), energy 1.3 (was 1.0), `ChamberAmbientEnergy` 0.45 (was 0.62), shadow normal bias 2.0 (was 3.0; raise if acne returns), SSAO on (radius 1.4, intensity 2.4, light affect 0.35).
- vertsPerUnit 2.5 to 3.0 so the 0.9 unit octave has samples.
- Shader gained a `relief` group (`slope_darken`, `contour_strength`, `contour_spacing`, `contour_fade_start/end`), all defaulting to off so the atlas is untouched. Slope uses the world-space normal via INV_VIEW_MATRIX.

Known consequences: rivers and roads follow SampleGround and so ride the crags on hill tiles; crag from a mountain next to a shore can lift a bed above the waterline (reads as rocks in the shallows). Not measured: none of this was seen rendered; tune from a screenshot.

## Increment 10d: the resumed castle stood off the map

"No way onward" on a tile with six passable neighbours. `RestoreSortie` fed the slot's WORLD offset coords through `GridLocalOf`, which is the identity (the combat router saves LOCAL coords, so it never needed to convert). The party was initialised at a local coordinate far off the loaded disc: `GetNeighbors` returned nothing, both blind passes failed, and the stride halted on its first tick. The 3D token stayed drawn on the staging tile because its local-to-world lookup failed and never updated, which is why the screenshot looked like a castle refusing to leave its own seat. Now `_window.LocalOf(slot.X, slot.Y)`, plus a PrintErr if the result is not in the window after the recenter.

## Increment 11: ELSEWHERE

A second, smaller block under the main HUD status panel listing every force the stone is not aimed at: name (castle in arcane blue, parties in gold, matching the map markers), a right-aligned status line, the numbers, and a 4 px furnace bar. Frozen forces report their `SortieState` (fuel or rations, Hull or Health, "holding station" or "holding, N moons"); a parked castle reports `CastleFuel/CastleMaxFuel` and parked / resupply / under attack (its Hull between sorties is not persisted anywhere, so none is shown); a party between sorties reports only where it is (awaiting orders, marching to, not sited). Hangs under the main panel via its `Resized` signal so the info line wrapping cannot push the two apart or overlap them. Refreshed from `UpdateUI`. `UITheme` gained `FurnaceBed`, `FurnaceEmber`, `ElsewhereHeader`, `ElsewhereStatus`.

## Increment 12: Make Camp, Bank the furnace, and the Working skeleton

**The gap.** `ParkCastle` had two callers, both inside the dry-furnace dialog. The parked state (free upkeep, a waypoint the parties can step to, exposed to `CastleThreats`) could only be reached by burning the tank dry; the alternative was leaving the castle frozen mid-sortie at 3 fuel per lunation with a raid clock. For the zone loop (park the castle beside a zone, work it with parties for several moons) that is a trap.

**Make Camp** (expedition HUD, row eight, castle only): confirm dialog quoting the fuel kept and the resupply bill, then `ParkCastle`. Look Up moves to row nine (396) and the desk panel to 448.

**Bank the furnace** (strategic FORCES chrome, visible when the castle is selected and its slot is live): `ExpeditionAnchors.BankFrozenCastle` does the scene park's bookkeeping without the scene: tank to `CastleFuel/CastleMaxFuel`, un-banked earnings to `CastleHold`, `ParkCastleAt` on the slot's tile, slot cleared. `RepairLunationsFor` is now one formula shared by both parks. Not done from the strategic side: `RunEventLog.End` for the frozen run's journal (the log belongs to the scene that opened it).

**Working state skeleton.** `FieldPartyState.Working = 4`; `FieldParty.WorkKind / WorkZoneId / WorkLunationsLeft / WorkLunationsTotal`; new `FieldWork` (`CanBegin`, `Begin`, `Cancel`, `Tick`, `Describe`, and `OnComplete` with one empty case per future zone type, default posting a desk Note). `RunLunationTick` calls `FieldWork.Tick` after `FieldMarch.Tick`. `FieldMarch.CanOrderAtAll` refuses a working party ("stop the work first"). Roster, WhatResolves and ELSEWHERE all describe work. Verbs: "Stop work" (real) and "Survey here (debug)" (DebugMode only, two lunations, the stand-in until zones exist). Save assert covers the four fields. `ScryInbox.Post` is now the single desk writer; `SortieThreats` routes through it.

**Rulings still open, each with one landing spot:** cost per lunation (in `FieldWork.Tick`; needs a persisted party ration pool), yield (`OnComplete` cases), the threat table for a camped party (beside `SortieThreats`, with a parked castle within N tiles as defenders).
