using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Cover.cs
//
// Purpose:        Directional cover on the hex grid. A defender is
//                 covered against an attacker when the neighbour tile
//                 on the defender's side facing the attacker holds an
//                 obstacle. Cover is asymmetric by design: the shooter
//                 gains nothing from the defender's wall, and a shooter
//                 on any other side of the defender is flanking.
// Layer:          Systems / Combat / Terrain
// Collaborators:  TileData (CoverKind, AuthoredCover, BlocksLineOfSight),
//                 Unit (map objects supply Low cover), CombatManager
//                 (damage path reads CoverBetween), TargetSelectors
//                 (burst fill reads CoverAt)
// See:            docs/cover_and_zoc_v1.md
// ============================================================

public partial class HexGridManager
{
    /// <summary>Cosine of the widest angle (30 degrees plus slack) between the
    /// attack vector and a neighbour direction for that neighbour to count as
    /// "facing" the attacker. Hex directions sit 60 degrees apart, so at most two
    /// neighbours qualify, and only when the attacker sits on a corner bisector.</summary>
    private const float CoverFacingCos = 0.84f;   // cos(32.9 deg)

    /// <summary>Effective cover an obstacle tile provides to units beside it.
    /// High: the tile blocks sight (wall, rock, tree, pillar, thicket).
    /// Low: authored low cover, rubble, a sapling, or a map object that does not
    /// block sight (brazier, cask, ward stone). Units are never cover.</summary>
    public CoverKind CoverAt(TileData tile)
    {
        if (tile == null)
            return CoverKind.None;
        if (tile.BlocksLineOfSight)
            return CoverKind.High;
        if (tile.AuthoredCover == CoverKind.Low)
            return CoverKind.Low;
        // Rubble arrives two ways: the create_rubble card verb (TerrainModifier)
        // and a toppled pillar or collapse event (ObstacleKind, walkable at cost 2).
        if (tile.TerrainModifier == "rubble" || tile.ObstacleKind == "rubble")
            return CoverKind.Low;
        if (tile.GrowthStage == 1)
            return CoverKind.Low;
        if (tile.Occupant != null && tile.Occupant.IsMapObject && tile.Occupant.Stats.IsAlive)
            return CoverKind.Low;
        return CoverKind.None;
    }

    public CoverKind CoverAt(Vector2I coord)
        => Tiles.TryGetValue(coord, out var t) ? CoverAt(t) : CoverKind.None;

    /// <summary>The defender's neighbour coords that face <paramref name="attacker"/>:
    /// the one direction nearest the attack vector, or two when the attacker sits on
    /// the bisector between them. Empty when the coords coincide.</summary>
    public List<Vector2I> FacingNeighbors(Vector2I defender, Vector2I attacker)
    {
        var result = new List<Vector2I>(2);
        if (defender == attacker)
            return result;

        var d = AxialToWorld(defender);
        var a = AxialToWorld(attacker);
        var toAttacker = new Vector2(a.X - d.X, a.Z - d.Z);
        if (toAttacker.LengthSquared() < 0.0001f)
            return result;
        toAttacker = toAttacker.Normalized();

        foreach (var dir in HexDirs)
        {
            var n = AxialToWorld(defender + dir);
            var v = new Vector2(n.X - d.X, n.Z - d.Z).Normalized();
            if (v.Dot(toAttacker) >= CoverFacingCos)
                result.Add(defender + dir);
        }
        return result;
    }

    /// <summary>Cover <paramref name="defender"/> enjoys against a shot from
    /// <paramref name="attacker"/>: the best cover among the facing neighbours.
    /// The attacker's own tile never counts (an adjacent attacker is past the wall).
    /// Height rule (extends the 2026-08-11 high-ground ruling): an attacker standing
    /// higher than the defender shoots over Low cover, so Low degrades to None.
    /// High cover is never negated by height. A defender on a raised tile keeps its
    /// cover regardless.</summary>
    public CoverKind CoverBetween(Vector2I defender, Vector2I attacker)
    {
        var best = CoverKind.None;
        foreach (var n in FacingNeighbors(defender, attacker))
        {
            if (n == attacker)
                continue;
            var c = CoverAt(n);
            if (c > best)
                best = c;
        }

        if (best == CoverKind.Low
            && Tiles.TryGetValue(defender, out var dt)
            && Tiles.TryGetValue(attacker, out var at)
            && at.Height > dt.Height)
        {
            best = CoverKind.None;
        }
        return best;
    }

    /// <summary>True when <paramref name="attacker"/> has a shot at the defender
    /// that no facing neighbour on the defender's side can flank-protect, i.e.
    /// <see cref="CoverBetween"/> is None. Used by AI scoring and UI markers.</summary>
    public bool IsFlanked(Vector2I defender, Vector2I attacker)
        => CoverBetween(defender, attacker) == CoverKind.None;

    /// <summary>Best cover a unit standing on <paramref name="coord"/> would have
    /// against ANY of <paramref name="threats"/>. Worst case across threats matters
    /// more for AI tile choice than the average, so this returns the minimum.</summary>
    public CoverKind WorstCoverAgainst(Vector2I coord, IEnumerable<Vector2I> threats)
    {
        var worst = CoverKind.High;
        bool any = false;
        foreach (var t in threats)
        {
            any = true;
            var c = CoverBetween(coord, t);
            if (c < worst)
                worst = c;
            if (worst == CoverKind.None)
                break;
        }
        return any ? worst : CoverKind.None;
    }

    // ── Burst fill ──────────────────────────────────────────────────────────

    /// <summary>Extra spread steps a burst spends to climb Low cover. A radius-2
    /// blast reaches one tile past a low wall instead of two.</summary>
    public const int BurstLowCoverStep = 1;

    /// <summary>Flood fill from <paramref name="origin"/> through open ground up to
    /// <paramref name="radius"/> spread steps. Returns coord to spread cost. This is
    /// what aoe, ring, and cone use instead of raw hex distance, so a blast fills a
    /// courtyard, wraps around a pillar, and stops at a wall.
    /// Rules: a High-cover tile (blocks sight) is never entered. A Low-cover tile is
    /// entered at +<see cref="BurstLowCoverStep"/> and spreads on from there. Water,
    /// hazards, units, and elevation do not slow a burst. The origin is always
    /// included at cost 0, even when it is itself an obstacle (a shattered crystal
    /// bursts from its own tile). Height is ignored on purpose: a blast on a ridge
    /// still washes down it; the high-ground reward lives in Bolt range, not here.</summary>
    public Dictionary<Vector2I, int> BurstFill(Vector2I origin, int radius)
    {
        var cost = new Dictionary<Vector2I, int>();
        if (radius < 0 || !Tiles.ContainsKey(origin))
            return cost;

        cost[origin] = 0;
        var frontier = new List<Vector2I> { origin };

        // Dijkstra on a tiny graph with edge weights 1 or 2: a plain list scan is
        // cheaper than a heap at battlefield sizes (r5 map = 91 tiles).
        while (frontier.Count > 0)
        {
            int bestIdx = 0;
            for (int i = 1; i < frontier.Count; i++)
                if (cost[frontier[i]] < cost[frontier[bestIdx]])
                    bestIdx = i;
            var current = frontier[bestIdx];
            frontier.RemoveAt(bestIdx);
            int here = cost[current];

            foreach (var dir in HexDirs)
            {
                var next = current + dir;
                if (!Tiles.TryGetValue(next, out var tile))
                    continue;
                var cover = CoverAt(tile);
                if (cover == CoverKind.High)
                    continue;
                int step = 1 + (cover == CoverKind.Low ? BurstLowCoverStep : 0);
                int total = here + step;
                if (total > radius)
                    continue;
                if (cost.TryGetValue(next, out var known) && known <= total)
                    continue;
                cost[next] = total;
                frontier.Add(next);
            }
        }
        return cost;
    }

    /// <summary>Coords a burst of <paramref name="radius"/> from <paramref name="origin"/>
    /// reaches. Convenience over <see cref="BurstFill"/> for callers that only need
    /// membership (highlights, "who is in the blast").</summary>
    public HashSet<Vector2I> BurstReach(Vector2I origin, int radius)
        => new HashSet<Vector2I>(BurstFill(origin, radius).Keys);

    /// <summary>Coords at EXACTLY <paramref name="radius"/> spread steps: the ring a
    /// burst's outer edge draws once walls and low cover have bent it. A ring that
    /// meets a wall is simply shorter on that side.</summary>
    public HashSet<Vector2I> BurstRing(Vector2I origin, int radius)
    {
        var ring = new HashSet<Vector2I>();
        foreach (var kv in BurstFill(origin, radius))
            if (kv.Value == radius)
                ring.Add(kv.Key);
        return ring;
    }

    /// <summary>True when the tile has at least one neighbour that provides cover
    /// from some direction. Cheap "is this a cover tile" test for highlights.</summary>
    public bool HasAnyCover(Vector2I coord)
    {
        foreach (var dir in HexDirs)
            if (CoverAt(coord + dir) != CoverKind.None)
                return true;
        return false;
    }
}
