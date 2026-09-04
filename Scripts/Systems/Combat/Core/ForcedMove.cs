using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ForcedMove.cs
//
// Purpose:        The one resolver every push, shove, and slam goes
//                 through (forced_movement_v1). A shove travels in a
//                 STRAIGHT hex line and stops at the first thing it
//                 hits: an obstacle, a unit, a map object, the map edge,
//                 or a cliff going up. Hitting something hurts, scaled
//                 by the momentum the shove still had, and hitting a
//                 unit hurts both bodies and passes one tile of shove
//                 on. Every step is a Forced entry so fire sears, storm
//                 conducts, frost slides, stone anchors, and falls all
//                 fire. Pulls are positioning and keep their own slide
//                 rule, so they do not use this.
// Layer:          Systems / Combat / Core
// Collaborators:  Unit.PlaceOnTile, MoveContext, HexGridManager
//                 (GetTile, Distance, hex line), TileEntryReactions
//                 (brazier spill), PushEffect / PushAimedEffect /
//                 PushDamageEffect / DashEffect / enemy Shove intent
// See:            docs/forced_movement_v1.md
// ============================================================

public static class ForcedMove
{
    /// <summary>Collision damage per tile of shove the victim did NOT travel, when
    /// the card authored none. A 3-tile push that stops on the first tile hits at
    /// 2, one that stops on the last hits at 0: momentum, not a flat tax. Authored
    /// collision_damage overrides this when larger.</summary>
    public const int MomentumDamagePerTile = 1;

    public sealed class Result
    {
        public int Pushed;
        public bool Collided;
        /// <summary>What was hit: a living unit or map object, else null (wall, edge, cliff).</summary>
        public Unit HitUnit;
        public string HitWhat = "";
        public int CollisionDamage;
    }

    /// <summary>The unit step that continues the line from <paramref name="origin"/>
    /// through <paramref name="victim"/>: the direction a shove "away from origin"
    /// travels. Adjacent origins give the exact hex direction; further origins take
    /// the hex-line direction rounded to the nearest of the six. Zero when they
    /// coincide.</summary>
    public static Vector2I StepAwayFrom(HexGridManager grid, Vector2I origin, Vector2I victim)
    {
        if (origin == victim)
            return Vector2I.Zero;
        var delta = victim - origin;
        if (Math.Abs(delta.X) <= 1 && Math.Abs(delta.Y) <= 1 && Math.Abs(delta.X + delta.Y) <= 1)
            return delta;   // already a unit hex step
        int idx = HexDirection.Pick(origin, victim, 6);
        return HexDirection.All[idx];
    }

    /// <summary>The unit step from <paramref name="from"/> toward <paramref name="aim"/>.</summary>
    public static Vector2I StepToward(HexGridManager grid, Vector2I from, Vector2I aim)
        => StepAwayFrom(grid, aim, from) * -1;

    /// <summary>Shove <paramref name="victim"/> up to <paramref name="tiles"/> tiles along
    /// <paramref name="dir"/>. Immovable map objects do not move. A living unit or map
    /// object on the path is a collision: both take the collision damage and the
    /// struck unit is chained one tile further along the same line when it can be.
    /// <paramref name="authoredCollision"/> is the card's collision_damage (0 = use
    /// momentum). <paramref name="ctx"/> may be null for a fresh scope.</summary>
    public static Result Push(HexGridManager grid, Unit victim, Vector2I dir, int tiles,
                              int authoredCollision = 0, MoveContext ctx = null, Action<string> log = null)
    {
        var r = new Result();
        if (grid == null || victim?.CurrentTile == null || dir == Vector2I.Zero || tiles <= 0)
            return r;
        if (victim.IsMapObject && !victim.Pushable)
        {
            log?.Invoke($"[Push] {victim.Name} is immovable.");
            return r;
        }

        ctx ??= new MoveContext(grid);

        for (int i = 0; i < tiles; i++)
        {
            if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
                break;
            if (victim.CurrentTile == null || !victim.Stats.IsAlive)
                break;

            var cur = victim.CurrentTile;
            var next = grid.GetTile(cur.Axial + dir);

            if (next == null)
            {
                r.Collided = true; r.HitWhat = "the edge";
                break;
            }
            if (next.Height - cur.Height >= 2)
            {
                r.Collided = true; r.HitWhat = "the cliff";
                break;
            }
            if (next.IsOccupied && next.Occupant != null && next.Occupant != victim && next.Occupant.Stats.IsAlive)
            {
                r.Collided = true; r.HitUnit = next.Occupant; r.HitWhat = next.Occupant.Name;
                break;
            }
            if (!next.CanEnter(victim))
            {
                r.Collided = true;
                r.HitWhat = !string.IsNullOrEmpty(next.ObstacleKind) ? next.ObstacleKind.Replace('_', ' ')
                          : next.TerrainType == TileTerrainType.Water ? "the water"
                          : "the ground";
                // Water is a different kind of stop: the unit is halted at the bank,
                // it does not slam. No damage for a water stop.
                if (next.TerrainType == TileTerrainType.Water && !next.IsBlocked)
                    r.HitWhat = "";
                break;
            }

            ctx.ForcedTilesRemaining--;
            victim.PlaceOnTile(next, MovementKind.Forced, ctx);
            r.Pushed++;
            if (ctx.HaltForced)
                break;   // Stone Anchors, or the cap
        }

        if (r.Collided && !string.IsNullOrEmpty(r.HitWhat))
        {
            int remaining = tiles - r.Pushed;
            r.CollisionDamage = Math.Max(authoredCollision, remaining * MomentumDamagePerTile);
            if (r.CollisionDamage > 0)
            {
                victim.ApplyDamage(r.CollisionDamage);
                log?.Invoke($"[Push] {victim.Name} shoved {r.Pushed} tile(s) into {r.HitWhat}: {r.CollisionDamage} damage.");
                // A body thrown into a breakable wall cracks the wall too (map_pressure_v2).
                if (r.HitUnit == null && victim.CurrentTile != null)
                {
                    var wall = grid.GetTile(victim.CurrentTile.Axial + dir);
                    if (wall != null && wall.IsBlocked && wall.ObstacleHp > 0)
                        grid.DamageObstacle(wall, r.CollisionDamage, log);
                }
                if (r.HitUnit != null && r.HitUnit.Stats.IsAlive)
                {
                    r.HitUnit.ApplyDamage(r.CollisionDamage);
                    log?.Invoke($"[Push] {r.HitUnit.Name} is struck for {r.CollisionDamage}.");
                    // Chain shove depth 1 (tile_interaction_spec §4.2), same line.
                    if (!ctx.HaltForced && ctx.ForcedTilesRemaining > 0 && r.HitUnit.CurrentTile != null
                        && !(r.HitUnit.IsMapObject && !r.HitUnit.Pushable))
                    {
                        var chainNext = grid.GetTile(r.HitUnit.CurrentTile.Axial + dir);
                        if (chainNext != null && chainNext.CanEnter(r.HitUnit)
                            && chainNext.Height - r.HitUnit.CurrentTile.Height < 2)
                        {
                            ctx.ForcedTilesRemaining--;
                            r.HitUnit.PlaceOnTile(chainNext, MovementKind.Forced, ctx);
                            log?.Invoke($"[Push] chain: {r.HitUnit.Name} shoved 1 tile further.");
                        }
                    }
                }
            }
            else
            {
                log?.Invoke($"[Push] {victim.Name} shoved {r.Pushed} tile(s), stopped by {r.HitWhat}.");
            }
        }
        else
        {
            log?.Invoke($"[Push] {victim.Name} shoved {r.Pushed} tile(s).");
        }

        // A shoved Ember Brazier spills fire where it lands and one tile on.
        if (r.Pushed > 0 && victim.IsMapObject && victim.MapObjectKind == "ember_brazier"
            && victim.CurrentTile != null)
        {
            TileEntryReactions.ImbueTile(victim.CurrentTile, TileElementType.Fire);
            var on = grid.GetTile(victim.CurrentTile.Axial + dir);
            if (on != null && on.IsWalkable && !on.IsBlocked)
                TileEntryReactions.ImbueTile(on, TileElementType.Fire);
        }

        return r;
    }

    /// <summary>Predict the tiles a push would cross without moving anything (for
    /// telegraphs and previews). Stops where <see cref="Push"/> would stop; slides
    /// and anchors that only exist at resolution are not modelled.</summary>
    public static List<Vector2I> Predict(HexGridManager grid, Unit victim, Vector2I dir, int tiles)
    {
        var path = new List<Vector2I>();
        if (grid == null || victim?.CurrentTile == null || dir == Vector2I.Zero)
            return path;
        if (victim.IsMapObject && !victim.Pushable)
            return path;
        var cur = victim.CurrentTile;
        for (int i = 0; i < tiles; i++)
        {
            var next = grid.GetTile(cur.Axial + dir);
            if (next == null || next.Height - cur.Height >= 2 || !next.CanEnter(victim))
                break;
            path.Add(next.Axial);
            cur = next;
        }
        return path;
    }
}
