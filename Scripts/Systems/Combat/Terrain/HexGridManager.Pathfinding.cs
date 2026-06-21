using Godot;
using System;
using System.Collections.Generic;

// HexGridManager.Pathfinding.cs — reachability, move-cost, line-of-sight, AI step queries, cliff rules
// Partial of HexGridManager. Split out for navigability; behaviour-neutral.
public partial class HexGridManager
{
    /// <summary>True when the height gap between two tiles exceeds the cliff threshold.</summary>
    public bool IsCliff(TileData a, TileData b) =>
        a != null && b != null && Math.Abs(a.Height - b.Height) > CliffHeightThreshold;

    /// <summary>Movement legality of one step between adjacent coords, per the cliff rule.</summary>
    private bool StepAllowed(Vector2I from, Vector2I to)
    {
        if (!BlockMovementAtCliffs)
            return true;
        if (!Tiles.TryGetValue(from, out var a) || !Tiles.TryGetValue(to, out var b))
            return true;
        return Math.Abs(a.Height - b.Height) <= CliffHeightThreshold;
    }

    public HashSet<Vector2I> GetReachableTiles(Unit unit)
    {
        var result = new HashSet<Vector2I>();
        if (unit == null || unit.CurrentTile == null)
            return result;
        if (!unit.CanMove())
            return result;  // no AP = no highlights

        var start = unit.CurrentTile.Axial;
        int budget = unit.Stats.BaseSpeed;  // always BaseSpeed, not MovePoints

        var frontier = new Queue<(Vector2I coord, int costUsed)>();
        var bestCost = new Dictionary<Vector2I, int> { [start] = 0 };
        frontier.Enqueue((start, 0));

        while (frontier.Count > 0)
        {
            var (current, costUsed) = frontier.Dequeue();
            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;
                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;
                if (!StepAllowed(current, neighbor))
                    continue;
                if (tile.IsOccupied && neighbor != start)
                    continue;

                int stepCost = Mathf.Max(1, tile.MoveCost);
                int newCost = costUsed + stepCost;
                if (newCost > budget)
                    continue;

                if (bestCost.TryGetValue(neighbor, out int oldCost) && oldCost <= newCost)
                    continue;

                bestCost[neighbor] = newCost;
                frontier.Enqueue((neighbor, newCost));
                if (neighbor != start)
                    result.Add(neighbor);
            }
        }

        return result;
    }

    // Pathfinding

    /// <summary>
    /// Returns a dictionary of reachable tile coords → movement cost to reach them.
    /// Used to drive cost-coloured highlighting.
    /// </summary>
    public Dictionary<Vector2I, int> GetReachableTilesWithCost(Unit unit)
    {
        var result = new Dictionary<Vector2I, int>();
        if (unit?.CurrentTile == null)
            return result;

        var start = unit.CurrentTile.Axial;
        int maxMove = unit.MoveRange;

        // Priority queue: (coord, costSoFar) ordered by lowest cost first
        var frontier = new PriorityQueue<Vector2I, int>();
        var bestCost = new Dictionary<Vector2I, int>();

        frontier.Enqueue(start, 0);
        bestCost[start] = 0;

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            int costSoFar = bestCost[current];

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;
                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;
                if (!StepAllowed(current, neighbor))
                    continue;
                if (tile.IsOccupied && neighbor != start)
                    continue;

                int stepCost = Mathf.Max(1, tile.MoveCost);
                int newCost = costSoFar + stepCost;

                if (newCost > maxMove)
                    continue;

                if (bestCost.TryGetValue(neighbor, out int old) && old <= newCost)
                    continue;

                bestCost[neighbor] = newCost;
                frontier.Enqueue(neighbor, newCost);

                if (neighbor != start)
                    result[neighbor] = newCost;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the minimum movement point cost for the given unit to reach dest,
    /// respecting tile MoveCost. Returns -1 if unreachable.
    /// </summary>
    public int GetMoveCostTo(Unit unit, TileData dest)
    {
        if (unit?.CurrentTile == null || dest == null)
            return -1;

        var start = unit.CurrentTile.Axial;
        var goal = dest.Axial;

        if (start == goal)
            return 0;

        var bestCost = new Dictionary<Vector2I, int> { [start] = 0 };
        var frontier = new Queue<(Vector2I coord, int cost)>();
        frontier.Enqueue((start, 0));

        while (frontier.Count > 0)
        {
            var (current, costSoFar) = frontier.Dequeue();

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;

                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;

                if (!StepAllowed(current, neighbor))
                    continue;

                // Allow start tile, block other occupied tiles
                if (tile.IsOccupied && neighbor != start)
                    continue;

                int stepCost = Mathf.Max(1, tile.MoveCost);
                int newCost = costSoFar + stepCost;

                if (bestCost.TryGetValue(neighbor, out int oldCost) && oldCost <= newCost)
                    continue;

                bestCost[neighbor] = newCost;

                if (neighbor == goal)
                    continue; // found it, don't expand further unnecessarily

                frontier.Enqueue((neighbor, newCost));
            }
        }

        return bestCost.TryGetValue(goal, out int finalCost) ? finalCost : -1;
    }

    /// <summary>
    /// Returns true if there is a clear line of sight between two axial coords.
    /// Traces the hex line and checks BlocksLineOfSight on each tile crossed.
    /// The start and end tiles themselves are not checked.
    /// </summary>
    public bool HasLineOfSight(Vector2I from, Vector2I to)
    {
        // Use cube coordinate lerp to trace the line between hexes
        var steps = Distance(from, to);
        if (steps == 0)
            return true;

        for (int i = 1; i < steps; i++)
        {
            float t = (float)i / steps;

            // Lerp in cube coords
            float ax = from.X, az = from.Y, ay = -ax - az;
            float bx = to.X, bz = to.Y, by = -bx - bz;

            float lx = ax + (bx - ax) * t;
            float ly = ay + (by - ay) * t;
            float lz = az + (bz - az) * t;

            // Round to nearest cube coord
            int rx = Mathf.RoundToInt(lx);
            int ry = Mathf.RoundToInt(ly);
            int rz = Mathf.RoundToInt(lz);

            // Fix rounding to maintain x+y+z=0
            float dx = Mathf.Abs(rx - lx);
            float dy = Mathf.Abs(ry - ly);
            float dz = Mathf.Abs(rz - lz);

            if (dx > dy && dx > dz)
                rx = -ry - rz;
            else if (dy > dz)
                ry = -rx - rz;
            else
                rz = -rx - ry;

            var coord = new Vector2I(rx, rz);
            if (!Tiles.TryGetValue(coord, out var tile))
                continue;
            if (tile.BlocksLineOfSight)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the first step a unit should take to move toward a goal,
    /// navigating around obstacles via BFS. Returns null if no path exists.
    /// </summary>
    public TileData GetFirstStepToward(Unit unit, Vector2I goal)
    {
        if (unit?.CurrentTile == null)
            return null;

        var start = unit.CurrentTile.Axial;
        if (start == goal)
            return null;

        var visited = new Dictionary<Vector2I, Vector2I>(); // coord → came from
        var queue = new Queue<Vector2I>();

        queue.Enqueue(start);
        visited[start] = start;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.ContainsKey(neighbor))
                    continue;

                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;

                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;

                if (!StepAllowed(current, neighbor))
                    continue;

                // Allow occupied tiles in pathfinding (unit may move away)
                // but don't allow the goal tile to be blocked by a non-target unit
                if (tile.IsOccupied && neighbor != goal)
                {
                    visited[neighbor] = current;
                    // Don't enqueue — can't pass through, but record for path tracing
                    continue;
                }

                visited[neighbor] = current;

                if (neighbor == goal)
                {
                    // Reconstruct path back to find first step
                    var step = neighbor;
                    while (visited[step] != start)
                        step = visited[step];
                    return GetTile(step);
                }

                queue.Enqueue(neighbor);
            }
        }

        return null; // no path found
    }

    /// <summary>
    /// Returns the first step toward the tile that gets the unit
    /// closest to desiredDist from the goal, navigating around obstacles.
    /// </summary>
    public TileData GetFirstStepToDistance(Unit unit, Vector2I goal, int desiredDist)
    {
        if (unit?.CurrentTile == null)
            return null;

        var start = unit.CurrentTile.Axial;

        // BFS the full reachable map (ignoring AP — we want the best destination)
        var visited = new Dictionary<Vector2I, Vector2I>();
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        visited[start] = start;

        Vector2I bestDest = start;
        int bestDelta = Math.Abs(Distance(start, goal) - desiredDist);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.ContainsKey(neighbor))
                    continue;
                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;
                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;
                if (!StepAllowed(current, neighbor))
                    continue;

                visited[neighbor] = current;
                queue.Enqueue(neighbor);

                int delta = Math.Abs(Distance(neighbor, goal) - desiredDist);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestDest = neighbor;
                }
            }
        }

        if (bestDest == start)
            return null;

        // Trace back to find first step from start
        var step = bestDest;
        while (visited.ContainsKey(step) && visited[step] != start)
            step = visited[step];

        return GetTile(step);
    }

    /// <summary>
    /// Returns the first step that moves the unit as far as possible
    /// from the goal while staying on a navigable path.
    /// </summary>
    public TileData GetFirstStepAwayFrom(Unit unit, Vector2I goal)
    {
        if (unit?.CurrentTile == null)
            return null;

        var start = unit.CurrentTile.Axial;

        var visited = new Dictionary<Vector2I, Vector2I>();
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        visited[start] = start;

        Vector2I bestDest = start;
        int bestDist = Distance(start, goal);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.ContainsKey(neighbor))
                    continue;
                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;
                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;
                if (!StepAllowed(current, neighbor))
                    continue;

                visited[neighbor] = current;
                queue.Enqueue(neighbor);

                int d = Distance(neighbor, goal);
                if (d > bestDist)
                { bestDist = d; bestDest = neighbor; }
            }
        }

        if (bestDest == start)
            return null;

        var step = bestDest;
        while (visited.ContainsKey(step) && visited[step] != start)
            step = visited[step];

        return GetTile(step);
    }
}
