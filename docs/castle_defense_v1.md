# Castle Defense v1 (mobile fortress F6)

Fractured Arcana. Built 2026-09-02. Implements mobile_fortress_expedition_spec §7.2 with the map and stations the spec deferred. Not yet run in-engine.

## 1. Rulings (Magos, 2026-09-02)

1. The enemy wins by destroying the Castle Heart: `protect` objective, Heart as the ward (spec ruling kept). Hull damage on loss follows the existing forced-recall path.
2. Manned slots are module-driven stations: each installed castle module with a `station` block claims a rampart tile, and a unit standing there at the turn boundaries gets its effect. No new verb: standing there is the action. This amends the spec's "crew stations dormant in ambush" ruling, which predates slots on the map.
3. The castle is a rim-hugging half ring with the gate facing the field, about a third of the map, backdrop towers past the rim. Superseded by v2 (§2): a raised deck with one gangway, the hull continuing off the map as a body.
4. The wizard arrives by waystone on round 3 (delay 2), reduced by the Wardroom crew station and Waystone Focus, floor 0.
   The delay only applies when at least one other controllable player unit is fielded; otherwise the wizard stays from round 1 and the log says why. Every timeline starts with such a unit: Brannoc Helm, the castle's helmsman (`CompanionRoster.StartingDriverId`), recruited free and placed in the party at cycle start. Until the wizard arrives the deck is not shown, the unit bar marks him "in the waystone", and reactions cannot be answered from his hand.

## 2. The map (`CastleDefenseCompiler.cs`, v2 body)

The first playtest read the castle as a fenced yard: a ring of walls on flat ground with a rock in it. v2 (2026-09-05) makes it a body.

Radius 7 hexagon. The Heart sits one tile in from the -X rim on the centre row. The ground is generated normally. Around the Heart, the **deck**: every tile within 2, stone, height 1. The ring at distance 3 is the deck's **edge**: also height 1, carrying `hull_bulwark` obstacles (Low cover, hp 12, breakable, one hex plate per tile so the curved ring never clips) on every tile but one. That one is the **gate**, at ground level (height 0), with the skirt tile in front of it as the gate foot. The ring at distance 4 (the **skirt**) is flattened to height 0 so the platform is exactly one step above the ground everywhere; attackers step up only through the gate, since every other edge tile is bulwark. Two `smoke_stack` pillars (High, indestructible) stand on the outer deck ring, one per flank, breaking sight across the deck. There are no gate doors (`entry: gangway`); the gate opening is the choke. (2026-09-07 revision after the first in-engine look: the earlier height-2 cliff deck read as a slab and the hull mass behind it blocked the camera.)

Past the rim the body continues as `backdrop_stamps` with kinds: one ring of low `hull` plates (hex prisms on the deck line, 2.2 tall, where the edge ring leaves the arena; kept low because the camera sits behind that rim), two `leg` columns further out on each flank going down past the vista floor, and two `stack` chimneys behind the Heart. Each stamp's tint varies by id so the parts mismatch. Placeholder geometry only; the silhouette is the point.

Anchors: the crew musters on the deck beside the gangway; the enemy anchor is the far rim. The field carries an approach lane to the gangway foot, two cover lines, two rock clusters, and a powder cask. Station tiles are outer deck ring tiles nearest the gangway, sides interleaved, never the tile beside the gangway top and never a stack.

**The castle acts.** The compiler authors `map_events` on the recipe so the body moves during the fight, every one telegraphed a round ahead:

| Event | Kind | Clock | What |
|---|---|---|---|
| `stomp_left`, `stomp_right` | `stomp` (new) | rounds 2 and 4, every 4 | A leg comes down on a field tile beside the flank: 6 damage to everything in radius 1, survivors thrown one tile outward through the resolver, footprint sinks one step. The feet are on the field at distance 5 from the Heart, so the disk never reaches the deck. |
| `furnace_vent` | `imbue_patch` | round 3, every 3 | Fire on the gangway foot, radius 1. |
| `hull_lurch` | `shift` with `ring` (new form) | round 5, every 5 | Every unit on the ring at distance 4 from the Heart (the field tiles pressed against the hull, and the gangway ramp) is shoved one tile straight away from the Heart, 2 collision damage. |

`ResolveRecipeCoord` accepts a literal `"q,r"` and, on castle recipes, `heart` and `gangway`, so the compiler can pin events to tiles it computed. Schema updated for all three (`stomp`, `ring`/`max_height`/`crater`, the coord forms).

The recipe is emitted as JSON in the city compiler's shape, registered under `castle_defense_<terrain>_<seed>`, and consumed by the existing siege machinery. `SiegeSpec` carries `heart` and `stations`; `SiegeBackdropStamp` gained `kind`, `height`, `lift`.

## 3. Runtime (`CombatManager.CastleDefense.cs`)

**Heart.** `SpawnObjectiveWard` spawns the ward at the compiled heart tile through `SpawnRegistryUnit` (team 0) when the recipe has one, else on the spawn side as before. `Data/Units/castle_heart.json`: 60 HP, 2 armour, immobile structure tags, never acts.

**Wizard arrival.** The wizard is fielded normally (so it gets the persistent deck), then translocated out after the ward spawns: no tile, hidden, `IsAwaitingArrival`, unselectable in deployment and play, skipped by deployment reset and by the first-unit auto-select. At the round boundary on or after the arrival round it steps onto the free tile nearest the Heart with translocation shock (AP zeroed that round). Delay = 2 + module `ambush_delay` magnitudes + Wardroom reduction (`PlayerSession.AmbushWizardDelayReduction`, set at deploy), floor 0.

**Heart pulse.** Once per fight, at the round boundary after the Heart first drops below half, every player unit standing on the deck gains 4 shield (`HeartPulse`). The castle fights back for its crew.

**Stations.** `Data/CastleModules/*.json`. A station is claimed by standing on its tile (`ApplyStationBonusFor` from `HandleUnitMoved` and at turn start). The ballista is a crew weapon with its own attack (ruling 2026-09-07: a separate attack, not a modifier on the basic strike): any player unit manning it fires from the **action bar** in the unit panel (`CombatManager.Actions.cs`, 2026-09-08): press "Fire Ballista (2 AP)", then click a target; Alt+click is the shortcut. The same bar carries Strike/Shoot and Shove for martials (Ctrl+click shortcut for Shove, plain click for Strike). A pressed button arms the action, the hover envelope follows the armed action, and right-click or Esc disarms. The bar also carries `Interact (1 AP)` when a lever or a breakable obstacle is adjacent (the tooltip says which: pull, hold or delay the lever per its mode, or hammer the wall for max(2, attack) a blow; a plain click on a lever also interacts, a plain click on a wall is always a move), `Brace (all AP)` for martials (+1 armour until the next turn start, cover armour refilled if in cover, counts as acted), and `Swap (free)` while The Dance is up. Working a hold or delay lever sets `MapEventDef.HeldByAction`, which the round boundary reads as held. `TryFireStation`: the station's `range` (5, +1 shooting down), `damage` (7), `ap` (2), `push` (1 tile straight back through the resolver, collision floor half the bolt), `shots` (1 a round, reloaded at turn start; stepping off and back on does not reload). Bolt delivery: cover soaks it, full cover stops it, line of sight required. Hovering an enemy shows the station's cyan envelope, ringed targets with block reasons, the bolt trace, and the hint line naming both the Alt+click shot and (for a martial keeper) the plain strike. The unit panel reads `BALLISTA rng 5 dmg 7 (2 AP, Alt+click)`, the floating station name turns green while manned, and the prop kicks on firing. Lantern, winch and brazier rack resolve at the turn boundaries. Station kinds:

| Module | Station | Effect while manned |
|---|---|---|
| Ballista Nest | ballista | crew weapon: Alt+click fires a bolt, range 5, damage 7, 2 AP, throws 1, one shot a round |
| Ward Lantern | ward_lantern | keeper +2 shield; every ally within 2 gets +1 cover armour (turn start) |
| Repair Winch | repair_winch | at the round boundary, the most damaged door or the Heart mends 4 |
| Brazier Rack | brazier_rack | at the round boundary, the three tiles outside the gate are set alight |

Plus three overworld-only modules from the spec (Auxiliary Furnace, Reinforced Keel, Waystone Focus). The loadout is `GuildSaveData.CastleModules`, backfilled with the starter pair (Ballista Nest, Ward Lantern) on old saves. No install UI yet (spec F5).

## 4. Routing

`ExpeditionManager`'s patrol-ambush path calls `CastleDefenseCompiler.Arm` for non-warfront interceptions: compiles, registers, sets `def.MapRecipe`, attaches the protect objective, flags the next combat. Warfront ambushes keep their siege routing. The debug launcher has a "castle DEFENSE (ambush: hold the Heart)" entry using the same call.

## 6. Motion (`CastleAnimator.cs`, v2 part 2)

Placeholder geometry, no art: everything is transforms, materials and particles from code. `CombatManager.CastleDefense.StartCastleAnimator` adds the node under the grid and binds it one frame late (after the deferred station props exist). It gathers the backdrop stamps by group (`castle_hull`, `castle_leg`, `castle_stack`) and the station props (`castle_station`, meta `station_kind`).

Idle: hull plates bob 0.035 out of phase and sway 0.6 degrees; legs bob less; each stack carries a `CpuParticles3D` smoke plume. The Heart gets an emissive material and an `OmniLight3D` that reaches the deck; its pulse period runs from 2.6 s at full health to 0.7 s near death, and both glow and light amplitude climb as it weakens. The ward lantern flickers. A manned ballista turns its bar toward the nearest enemy four times a second; unmanned it rests.

Beats: `ExecuteMapEvent` calls `OnCastleBeat(ev)` after every event, keyed on the compiler's ids. `stomp_left`/`stomp_right`: the matching leg lifts 1.4, slams, bounces, and the hull jolts. `hull_lurch`: the hull jerks toward the field and settles with an elastic tween. `furnace_vent`: every stack belches a one-shot burst. The Heart pulse event calls `FlareHeart`, a flare that decays over about a second.

## 5. Open items

1. v2 body not run in-engine. First look: does the deck spawn zone hold the crew (16 deck tiles minus Heart and two stacks); does the enemy never appear on the deck; does the gangway read as a ramp; do the hull stamps sit on the deck line (lift 1.2) rather than floating; do the stomp telegraphs show on the field and never on the deck; does the lurch throw attackers off the ramp. Motion is in (§6); check the plume height sits on the stack tops and that the Heart light does not wash out the deck.
2. Station tiles carry a floating amber Label3D with the module name (SpawnStationMarkers), and the move hover label reads "man the Ballista Nest" over one (StationHoverSuffix). A floor decal can replace the label once the meshes exist.
3. Siege pressure: AssignSiegeRoles retags every second melee attacker (melee_advance, melee_target_highest_hp, hold_until_near, range 1) to hunt_ward, so half the assault goes for the Heart through everything and the other half fights the crew. Runs at spawn and again at each round boundary for reinforcements; idempotent per unit. Ranged units keep their routine. Stations are still not valued by the AI.
4. Hull consequence on Heart death: the existing defeat path runs; the "castle limps home" recall consequence is the spec's F2 path and is not re-wired here.
5. Ambush enemy count is unchanged (spec: measure at F6, tune from data).
