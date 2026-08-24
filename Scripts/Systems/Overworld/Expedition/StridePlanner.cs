using Godot;
using System.Collections.Generic;

// ============================================================
// StridePlanner.cs
//
// Purpose:        A* pathfinding for Mobile Fortress Stride Orders (§3.4).
//                 The player clicks a distant tile and the castle strides
//                 there along a computed path; this finds that path over the
//                 SAME fuel-cost function the move actually charges, so the
//                 previewed route and its estimate can never disagree with
//                 what the world does (G1).
//
//                 Pure and data-driven: the caller supplies delegates for
//                 neighbours, orderability, edge cost, and the heuristic, so
//                 the planner has no dependency on the expedition's nodes and
//                 both the preview (hover) and the execution (click) can call
//                 the one function.
//
//                 Rules the caller encodes in its delegates (§3.4):
//                   - Plan only across SCRIED ground: Revealed tiles at real
//                     cost, Silhouette at a pessimistic flat cost, Hidden not
//                     orderable (and a Hidden destination is not orderable).
//                   - Water is impassable.
//                   - Known POI tiles carry a path-weight penalty so a stride
//                     routes AROUND encounters; a POI as the destination is
//                     ordered normally (the caller drops the penalty on goal).
// Layer:          System (pure helper; no nodes)
// Collaborators:  ExpeditionManager (delegates + execution),
//                 ExpeditionWindow3D (preview ribbon), OverworldMovementCost.
// ============================================================

/// <summary>A* over the overworld fuel-cost field for Stride Orders (§3.4).</summary>
public static class StridePlanner
{
    /// <summary>Plan a path from <paramref name="start"/> to <paramref name="goal"/>.
    /// Returns the path EXCLUDING start and ENDING at goal, or null if the goal is
    /// not orderable or no scried route exists. Empty list if start == goal.
    /// <para><paramref name="orderable"/>: may the stride traverse/stop on this tile
    /// (in-window, not water, not Hidden). <paramref name="edgeCost"/>: fuel to step
    /// from a to b, already including any POI penalty and the Silhouette flat cost.
    /// <paramref name="heuristic"/>: admissible lower bound to goal (hex distance;
    /// min edge cost is 1, so plain distance is admissible).</para></summary>
    public static List<Vector2I> Plan(
        Vector2I start, Vector2I goal,
        System.Func<Vector2I, IEnumerable<Vector2I>> neighbors,
        System.Func<Vector2I, bool> orderable,
        System.Func<Vector2I, Vector2I, int> edgeCost,
        System.Func<Vector2I, int> heuristic,
        int maxExpansions = 6000)
    {
        if (start == goal)
            return new List<Vector2I>();
        if (!orderable(goal))
            return null;

        var came = new Dictionary<Vector2I, Vector2I>();
        var g = new Dictionary<Vector2I, int> { [start] = 0 };
        var open = new PriorityQueue<Vector2I, int>();
        open.Enqueue(start, heuristic(start));
        var closed = new HashSet<Vector2I>();

        int expansions = 0;
        while (open.Count > 0 && expansions++ < maxExpansions)
        {
            var cur = open.Dequeue();
            if (cur == goal)
                return Reconstruct(came, start, goal);
            if (!closed.Add(cur))
                continue; // stale duplicate

            foreach (var nb in neighbors(cur))
            {
                if (closed.Contains(nb) || !orderable(nb))
                    continue;
                int tentative = g[cur] + Mathf.Max(1, edgeCost(cur, nb));
                if (!g.TryGetValue(nb, out int gn) || tentative < gn)
                {
                    g[nb] = tentative;
                    came[nb] = cur;
                    open.Enqueue(nb, tentative + heuristic(nb));
                }
            }
        }
        return null; // no route within the expansion budget
    }

    /// <summary>Total fuel a planned path costs, with the momentum discount folded
    /// in for the preview estimate (§3.4): from the 4th consecutive step onward each
    /// step's burn is reduced by 1 (floor 1). Assumes an uninterrupted stride — the
    /// live march re-derives per step and momentum resets on any halt.</summary>
    public static int FuelEstimate(Vector2I start, List<Vector2I> path,
                                   System.Func<Vector2I, Vector2I, int> edgeCost,
                                   bool applyMomentum = true)
    {
        if (path == null || path.Count == 0)
            return 0;
        int total = 0;
        var from = start;
        for (int i = 0; i < path.Count; i++)
        {
            int c = Mathf.Max(1, edgeCost(from, path[i]));
            if (applyMomentum && i >= 3)          // 4th consecutive step onward
                c = Mathf.Max(1, c - 1);
            total += c;
            from = path[i];
        }
        return total;
    }

    private static List<Vector2I> Reconstruct(Dictionary<Vector2I, Vector2I> came,
                                              Vector2I start, Vector2I goal)
    {
        var path = new List<Vector2I>();
        var cur = goal;
        while (cur != start)
        {
            path.Add(cur);
            if (!came.TryGetValue(cur, out cur))
                return null; // broken chain (shouldn't happen)
        }
        path.Reverse();
        return path;
    }
}
