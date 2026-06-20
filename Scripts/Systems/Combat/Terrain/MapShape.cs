using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// MapShape.cs
//
// Purpose:        Generates the SET of axial coordinates that make up a
//                 combat grid, decoupled from any W×H loop. A rectangular
//                 range of axial coords renders as a rhombus in world space;
//                 this lets the grid take other silhouettes (true rectangle,
//                 hexagon, triangle, eroded blob) while every downstream
//                 system keeps working, because they're all driven by the
//                 Tiles dictionary, not by grid bounds.
// Layer:          System (generation helper)
// Collaborators:  HexGridManager.GenerateBaseGrid (consumes the coord list)
// Notes:          Axial convention matches HexGridManager (flat-top, q = col).
//                 Blob erosion is seeded → reproducible for a given MapSeed.
// ============================================================

public enum MapShape
{
    Parallelogram, // the original rhombus (kept for compatibility)
    Rectangle,     // true rectangular silhouette via offset rows
    Hexagon,       // symmetric hex arena, centred on origin
    Triangle,      // wedge
    Blob           // organic / irregular coastline
}

public static class MapShapeBuilder
{
    private static readonly Vector2I[] Dirs =
    {
        new Vector2I(1, 0), new Vector2I(1, -1), new Vector2I(0, -1),
        new Vector2I(-1, 0), new Vector2I(-1, 1), new Vector2I(0, 1)
    };

    /// <summary>Builds the axial coordinate set for the requested shape.</summary>
    public static List<Vector2I> Build(MapShape shape, int width, int height, int radius, float erosion, RandomNumberGenerator rng)
    {
        return shape switch
        {
            MapShape.Parallelogram => Parallelogram(width, height),
            MapShape.Rectangle => Rectangle(width, height),
            MapShape.Hexagon => Hexagon(radius),
            MapShape.Triangle => Triangle(radius),
            MapShape.Blob => Blob(radius, erosion, rng),
            _ => Rectangle(width, height)
        };
    }

    // ── Shapes ──────────────────────────────────────────────────────────────

    private static List<Vector2I> Parallelogram(int width, int height)
    {
        var coords = new List<Vector2I>();
        for (int q = 0; q < width; q++)
            for (int r = 0; r < height; r++)
                coords.Add(new Vector2I(q, r));
        return coords;
    }

    /// <summary>
    /// True rectangle: each column's r-range is shifted by -(q/2) to cancel the
    /// q/2 shear in AxialToWorld, so the world-space outline is a rectangle (with
    /// the expected brick-row offset) instead of a rhombus.
    /// </summary>
    private static List<Vector2I> Rectangle(int width, int height)
    {
        var coords = new List<Vector2I>();
        for (int q = 0; q < width; q++)
        {
            int r0 = -(q / 2);
            for (int r = r0; r < r0 + height; r++)
                coords.Add(new Vector2I(q, r));
        }
        return coords;
    }

    /// <summary>Hexagonal arena of the given radius, centred on (0,0). Uses negative coords.</summary>
    private static List<Vector2I> Hexagon(int radius)
    {
        radius = Mathf.Max(1, radius);
        var coords = new List<Vector2I>();
        for (int q = -radius; q <= radius; q++)
        {
            int r1 = Math.Max(-radius, -q - radius);
            int r2 = Math.Min(radius, -q + radius);
            for (int r = r1; r <= r2; r++)
                coords.Add(new Vector2I(q, r));
        }
        return coords;
    }

    private static List<Vector2I> Triangle(int size)
    {
        size = Mathf.Max(1, size);
        var coords = new List<Vector2I>();
        for (int q = 0; q <= size; q++)
            for (int r = 0; r <= size - q; r++)
                coords.Add(new Vector2I(q, r));
        return coords;
    }

    /// <summary>
    /// Organic shape: a solid hexagonal core with its outer ring(s) eroded by a
    /// seeded noise field, producing an irregular coastline. The interior is
    /// protected so no holes form, then the largest connected component is kept
    /// as a final safety net.
    /// </summary>
    private static List<Vector2I> Blob(int radius, float erosion, RandomNumberGenerator rng)
    {
        radius = Mathf.Max(2, radius);
        var set = new HashSet<Vector2I>(Hexagon(radius));

        var noise = new FastNoiseLite
        {
            Seed = (int)rng.Randi(),
            Frequency = 0.30f,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex
        };

        // Higher erosion removes more of the rim → more ragged.
        float threshold = Mathf.Lerp(0.15f, 0.75f, Mathf.Clamp(erosion, 0f, 1f));

        var toRemove = new List<Vector2I>();
        foreach (var coord in set)
        {
            int ring = CubeDistanceFromOrigin(coord);
            if (ring < radius - 1)
                continue; // protect the interior

            float n = (noise.GetNoise2D(coord.X, coord.Y) + 1f) * 0.5f;
            if (n < threshold)
                toRemove.Add(coord);
        }

        foreach (var c in toRemove)
            set.Remove(c);

        return KeepLargestComponent(set);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CubeDistanceFromOrigin(Vector2I axial)
    {
        // axial (q,r) → cube (x=q, z=r, y=-q-r); distance to origin
        return (Math.Abs(axial.X) + Math.Abs(axial.Y) + Math.Abs(axial.X + axial.Y)) / 2;
    }

    private static List<Vector2I> KeepLargestComponent(HashSet<Vector2I> set)
    {
        var seen = new HashSet<Vector2I>();
        List<Vector2I> best = new();

        foreach (var start in set)
        {
            if (seen.Contains(start))
                continue;

            var component = new List<Vector2I>();
            var stack = new Stack<Vector2I>();
            stack.Push(start);
            seen.Add(start);

            while (stack.Count > 0)
            {
                var c = stack.Pop();
                component.Add(c);

                foreach (var d in Dirs)
                {
                    var nb = c + d;
                    if (set.Contains(nb) && !seen.Contains(nb))
                    {
                        seen.Add(nb);
                        stack.Push(nb);
                    }
                }
            }

            if (component.Count > best.Count)
                best = component;
        }

        return best;
    }
}
