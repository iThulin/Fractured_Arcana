# Forced Movement v1

Fractured Arcana. Built 2026-09-02 on top of cover_and_zoc_v1. Not yet run in-engine.

## 1. Why

Pushes were the most common positional verb in the card set (51 `push`, 6 `push_damage`, 15 `pull`, 28 cards with authored collision damage) and the map now has things worth pushing people into: low walls, casks, braziers, fire, ledges. Three things kept that from mattering:

1. The derived-direction pushes (`push`, `push_damage`) moved the victim to "the enterable neighbour farthest from the caster". A unit shoved toward a wall slid along it instead of hitting it. Collisions only happened when the victim was boxed in on every outward side.
2. Collision damage was zero unless the card authored it. Twenty-three push cards did not, so most shoves into walls did nothing.
3. The legacy `move`-with-targets path (`DashEffect`) placed the victim with the Teleport kind: no entry verbs, no slide, no sear, no fall.

Corrected record: the real `PushEffect` already used the Forced kind with mutual collision and chain shove. The first survey overstated the breakage; items 1 and 2 above are the real ones.

## 2. The resolver (`ForcedMove.cs`)

`ForcedMove.Push(grid, victim, dir, tiles, authoredCollision, ctx, log)`: straight along `dir` one hex per step through `PlaceOnTile(Forced)`, so every entry verb fires. Stops at the first of: map edge, a climb of 2 or more, a living unit or map object, an unenterable tile. Water stops without a slam. Immovable map objects never move.

Collision damage = max(authored `collision_damage`, tiles not travelled x `MomentumDamagePerTile` (1)). A 3-tile push that stops on the first step hits for 2; one that travels the full distance hits for nothing. A unit collision hurts both bodies and chains the struck unit one tile along the same line (spec §4.2, unchanged). A cask in the way is a bomb: it takes the collision and detonates on its own death effect.

Direction: `StepAwayFrom(origin, victim)` is the exact hex step when adjacent, else the hex-line direction rounded to the nearest of six. `StepToward` is its inverse. `Predict` walks the same rules without moving anything, for telegraphs and previews.

Routed through it: `push`, `push_aimed`, `push_damage` (both derived and aimed), the legacy `move`-with-targets branch, and the enemy Shove intent. `pull` and `pull_damage` keep their slide-toward rule: a pull is positioning, and dragging someone around a pillar toward you is the intent.

The brazier spill (fire where it lands plus one tile on) lives in the resolver now, so every shove of a brazier spills, not only the card push.

## 3. Body-check

Every martial unit can spend 1 AP (`MartialAPCosts.AttackMelee`) to shove an adjacent enemy one tile straight away: Ctrl+click (Cmd on Mac). Collision floor is `BodyCheckCollision` = half the shover's attack, minimum 2. Against open ground it is a weaker swing; into a cask, a fire tile, or off a two-step drop it is the better one. Hovering an adjacent enemy with a martial selected shows the strike damage and where the shove would land in the hint bar, and outlines the landing tile.

Brutes plan the same call (`PlanBodyCheck`): when adjacent to their mark and the shove would hit a cask or brazier, land on a hazard, or drop the victim two or more steps, and the estimate beats a plain swing, they telegraph a 1-tile Shove instead of an Attack. The gust elite's 3-tile shove is unchanged apart from now travelling straight. `EnemyIntent.ShoveTiles` / `ShoveCollision` carry the difference.

## 4. Enemy positional tags (built 2026-09-02)

**`skirmisher`.** In `ExecuteRangedIntent`, a skirmisher with a clear, in-range shot from where it stands fires FIRST, then spends whatever AP is left on `SkirmishRetreat`: back toward its range band, with `PositionalScore` supplying the cover bonus and the free-strike penalty, so one beside a low wall stays behind it and one caught in melee only breaks away when the strike is cheaper than staying. Without a clear shot it falls through to the old order (reposition, then shoot). Applied to every `ranged_kite` unit that is not `immobile` (12 units).

**`pinner`.** `PositionalScore` gives a melee pinner +60 for ending adjacent to an enemy caster (player-controlled, not martial), under one tile of progress so it shapes the arrival rather than stalling it. Scouts' flank tie-break now prefers a mark another friendly melee unit already holds (`IsHeldByFriendly`): the pinned caster pays a strike to turn on the scout and cannot back away from both. Applied to `melee_target_highest_hp` and `melee_hunt_wounded` units (9).

**Volatile targets.** `PlanRanger` calls `PickVolatileTarget`: a powder cask, resonant crystal, or ember brazier within range+1 with a clear shot, whose burst (radius 1 through the burst fill) would reach two or more players, or one when the burst outdamages the arrow, becomes the locked target. The telegraph sits on the object's tile, so the player sees what is about to blow and can step away or shove it.

Intent marker tokens: `SKIRM`, `PIN`.

## 5. Open items

1. Not run in-engine. Smoke test: Ember Bolt's push into a low wall (expect momentum damage and the wall named in the log); a shove into a powder cask (expect the cask to die and burst); Ctrl+click shove off a ridge on the Terraces (expect fall damage); the brute telegraphing » beside a brazier.
2. `pull` still slides around obstacles by design; if a pull into a pillar should hurt, give pulls the resolver with `SuppressFalling` and a collision floor of 0.
3. The card pips do not say "push" yet; the aim summary only knows the targeting. A "Push N" rider pip from the effect tree is a small follow-up.
4. Momentum damage is a global ruling at 1 per tile. If pushes feel too punishing on open ground it should drop to 0 and rely on authored values; if too weak, 2.
