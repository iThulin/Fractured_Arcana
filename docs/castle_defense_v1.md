# Castle Defense v1 (mobile fortress F6)

Fractured Arcana. Built 2026-09-02. Implements mobile_fortress_expedition_spec §7.2 with the map and stations the spec deferred. Not yet run in-engine.

## 1. Rulings (Magos, 2026-09-02)

1. The enemy wins by destroying the Castle Heart: `protect` objective, Heart as the ward (spec ruling kept). Hull damage on loss follows the existing forced-recall path.
2. Manned slots are module-driven stations: each installed castle module with a `station` block claims a rampart tile, and a unit standing there at the turn boundaries gets its effect. No new verb: standing there is the action. This amends the spec's "crew stations dormant in ambush" ruling, which predates slots on the map.
3. The castle is a rim-hugging half ring with the gate facing the field, about a third of the map, backdrop towers past the rim.
4. The wizard arrives by waystone on round 3 (delay 2), reduced by the Wardroom crew station and Waystone Focus, floor 0.
   The delay only applies when at least one other controllable player unit is fielded; otherwise the wizard stays from round 1 and the log says why. Every timeline starts with such a unit: Brannoc Helm, the castle's helmsman (`CompanionRoster.StartingDriverId`), recruited free and placed in the party at cycle start. Until the wizard arrives the deck is not shown, the unit bar marks him "in the waystone", and reactions cannot be answered from his hand.

## 2. The map (`CastleDefenseCompiler.cs`)

Radius 7 hexagon. The Heart sits one tile in from the -X rim on the centre row. Around it: courtyard (radius 1, stone floor), rampart ring (radius 2, stone, height 2, walkable), curtain wall (radius 3, `wall` obstacles, indestructible). The three mutually adjacent wall tiles furthest toward the field are the gate gap; `SiegeDoors` fields the gate door units there as it does for the city gate. Wall coordinates that fall past the rim go into `backdrop_wall`, so the castle continues off the map. The player musters in the courtyard inside the gate; the enemy anchor is the far rim. The field carries an approach lane, two cover lines, two rock clusters on the flanks, and a powder cask on the approach. Base terrain and the obstacle palette follow the overworld terrain the ambush happened on.

Station tiles are rampart tiles, gate flanks first (the towers), then spread along the arc alternating sides, one per installed station module.

The recipe is emitted as JSON in the city compiler's shape, registered under `castle_defense_<terrain>_<seed>`, and consumed by the existing siege machinery. `SiegeSpec` gained `heart` and `stations`.

## 3. Runtime (`CombatManager.CastleDefense.cs`)

**Heart.** `SpawnObjectiveWard` spawns the ward at the compiled heart tile through `SpawnRegistryUnit` (team 0) when the recipe has one, else on the spawn side as before. `Data/Units/castle_heart.json`: 60 HP, 2 armour, immobile structure tags, never acts.

**Wizard arrival.** The wizard is fielded normally (so it gets the persistent deck), then translocated out after the ward spawns: no tile, hidden, `IsAwaitingArrival`, unselectable in deployment and play, skipped by deployment reset and by the first-unit auto-select. At the round boundary on or after the arrival round it steps onto the free tile nearest the Heart with translocation shock (AP zeroed that round). Delay = 2 + module `ambush_delay` magnitudes + Wardroom reduction (`PlayerSession.AmbushWizardDelayReduction`, set at deploy), floor 0.

**Stations.** `Data/CastleModules/*.json`. Station kinds in v1:

| Module | Station | Effect while manned |
|---|---|---|
| Ballista Nest | ballista | martial attacks +2 range, +2 damage (turn start) |
| Ward Lantern | ward_lantern | keeper +2 shield; every ally within 2 gets +1 cover armour (turn start) |
| Repair Winch | repair_winch | at the round boundary, the most damaged door or the Heart mends 4 |
| Brazier Rack | brazier_rack | at the round boundary, the three tiles outside the gate are set alight |

Plus three overworld-only modules from the spec (Auxiliary Furnace, Reinforced Keel, Waystone Focus). The loadout is `GuildSaveData.CastleModules`, backfilled with the starter pair (Ballista Nest, Ward Lantern) on old saves. No install UI yet (spec F5).

## 4. Routing

`ExpeditionManager`'s patrol-ambush path calls `CastleDefenseCompiler.Arm` for non-warfront interceptions: compiles, registers, sets `def.MapRecipe`, attaches the protect objective, flags the next combat. Warfront ambushes keep their siege routing. The debug launcher has a "castle DEFENSE (ambush: hold the Heart)" entry using the same call.

## 5. Open items

1. Not run in-engine. First look: does the spawn zone BFS (3+ slots from the courtyard anchor) stay inside the walls; do the gate doors spawn on the gap; does the wizard reappear at the Heart on round 3; do station log lines fire.
2. Station tiles have no visual marker yet: a Label3D or a floor decal per station is the obvious next step, plus the station name in the hover cost label (MoveHoverSuffix).
3. Enemy AI does not value stations or the Heart specially beyond the existing structure targeting (brutes already prefer the nearest player unit, which is the door).
4. Hull consequence on Heart death: the existing defeat path runs; the "castle limps home" recall consequence is the spec's F2 path and is not re-wired here.
5. Ambush enemy count is unchanged (spec: measure at F6, tune from data).
