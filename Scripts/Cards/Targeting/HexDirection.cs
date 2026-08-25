using Godot;

// ============================================================
// HexDirection.cs
//
// Purpose:        Shared math helpers for hex direction vectors:
//                 the 6 axial unit vectors plus snap/projection
//                 routines used by every shape-based targeter
//                 (line, cone, ring, push, etc.).
// Layer:          Targeting
// Collaborators:  TargetSelectors.cs (consumers of All, Pick),
//                 TargetingHelpers.cs (paired helpers),
//                 HexGridManager.cs (axial coordinate system)
// See:            README §3 (hex grid is the spatial substrate)
// ============================================================
//
// Conventions: axial coordinates, flat-top hexes, 6 directions
// indexed clockwise starting from "east" (+x, 0). HexDirection.All
// is the canonical order. Do not reshuffle without updating
// callers that depend on the indexing (e.g. MaelstromEffect's
// rotation step).

/// <summary>Static utilities for the 6 axial hex directions and projecting arbitrary aim deltas onto them.</summary>
public static class HexDirection
{
    /// <summary>The 6 unit axial vectors, indexed clockwise starting from east (0). The MaelstromEffect rotation step depends on this ordering.</summary>
    public static readonly Vector2I[] All =
    {
        new( 1,  0),  // 0: east
        new( 1, -1),  // 1: northeast
        new( 0, -1),  // 2: northwest
        new(-1,  0),  // 3: west
        new(-1,  1),  // 4: southwest
        new( 0,  1),  // 5: southeast
    };

    /// <summary>
    /// Cube-space projection: which of the 6 directions points most strongly
    /// toward the aim tile? Stable tiebreaking: first-listed direction wins on ties.
    /// </summary>
    public static int BestToward(Vector2I origin, Vector2I aim)
    {
        var delta = aim - origin;
        int dq = delta.X;
        int dr = delta.Y;
        int ds = -dq - dr;

        int bestIdx = 0;
        int bestScore = int.MinValue;

        for (int i = 0; i < All.Length; i++)
        {
            var dir = All[i];
            int dirS = -dir.X - dir.Y;
            int score = dq * dir.X + dr * dir.Y + ds * dirS;
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    /// <summary>
    /// If the aim tile lies exactly along a hex axis from origin (within maxSteps),
    /// return that axis index. Otherwise -1.
    /// </summary>
    public static int SnapToAxis(Vector2I origin, Vector2I aim, int maxSteps)
    {
        var delta = aim - origin;
        for (int i = 0; i < All.Length; i++)
        {
            for (int mult = 1; mult <= maxSteps; mult++)
            {
                if (delta == All[i] * mult) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The everyday picker: snap to axis if the aim is exactly on one,
    /// otherwise project. Use this from targeters that need a single direction.
    /// </summary>
    public static int Pick(Vector2I origin, Vector2I aim, int maxSteps)
    {
        int snapped = SnapToAxis(origin, aim, maxSteps);
        return snapped >= 0 ? snapped : BestToward(origin, aim);
    }
}