using Godot;
using System.Collections.Generic;

// ============================================================
// HexCoord.cs
//
// Purpose:        Hex coordinate math for the world map. The world
//                 is stored as a RECTANGLE of offset coordinates
//                 (col, row) — Civ 6 style — but every distance,
//                 neighbor, and adjacency query runs in hex space.
//                 Layout: FLAT-TOP, odd-q (odd columns pushed down
//                 half a hex), matching OverworldHex / OverworldHexGrid.
//                 All conversions here are verified by offset<->axial
//                 round-trip, 6-neighbor interior, and exact disc-count
//                 checks before use.
// Layer:          Data (pure math, no nodes)
// Collaborators:  WorldData.cs, WorldGenerator.cs (distance/disc),
//                 StrategicView.cs (offset render position),
//                 WorldWindowBuilder (Phase 1c — disc slice)
// See:            single_world_refactor_v2.docx §3 (hex world)
//
// Conventions:
//   offset (col,row)  : rectangular storage index, row*Width+col
//   axial  (q,r)      : hex math space
//   cube   (x,y,z)    : x+y+z=0, used for distance
// ============================================================

public static class HexCoord
{
    // Flat-top axial neighbor directions (q, r).
    public static readonly (int dq, int dr)[] AxialDirections =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1),
    };

    // ── offset <-> axial (flat-top, odd-q) ───────────────────────────────
    public static (int q, int r) OffsetToAxial(int col, int row)
    {
        int q = col;
        int r = row - (col - (col & 1)) / 2;
        return (q, r);
    }

    public static (int col, int row) AxialToOffset(int q, int r)
    {
        int col = q;
        int row = r + (q - (q & 1)) / 2;
        return (col, row);
    }

    // ── axial -> cube, distance ──────────────────────────────────────────
    public static (int x, int y, int z) AxialToCube(int q, int r)
        => (q, r, -q - r);

    /// <summary>Hex distance between two AXIAL coords.</summary>
    public static int Distance(int q1, int r1, int q2, int r2)
    {
        var (ax, ay, az) = AxialToCube(q1, r1);
        var (bx, by, bz) = AxialToCube(q2, r2);
        return (Mathf.Abs(ax - bx) + Mathf.Abs(ay - by) + Mathf.Abs(az - bz)) / 2;
    }

    /// <summary>Hex distance between two OFFSET coords (the common case).</summary>
    public static int OffsetDistance(int col1, int row1, int col2, int row2)
    {
        var (q1, r1) = OffsetToAxial(col1, row1);
        var (q2, r2) = OffsetToAxial(col2, row2);
        return Distance(q1, r1, q2, r2);
    }

    // ── neighbors (offset in, offset out, bounded) ───────────────────────
    /// <summary>The up-to-6 in-bounds offset neighbors of an offset cell.</summary>
    public static List<(int col, int row)> Neighbors(int col, int row, int width, int height)
    {
        var (q, r) = OffsetToAxial(col, row);
        var result = new List<(int, int)>(6);
        foreach (var (dq, dr) in AxialDirections)
        {
            var (nc, nr) = AxialToOffset(q + dq, r + dr);
            if (nc >= 0 && nr >= 0 && nc < width && nr < height)
                result.Add((nc, nr));
        }
        return result;
    }

    // ── disc (the expedition window footprint) ───────────────────────────
    /// <summary>All in-bounds offset cells within hex radius R of an offset
    /// center. This is the expedition window footprint (R≈12 ≈ 469 tiles).</summary>
    public static List<(int col, int row)> Disc(int centerCol, int centerRow,
                                                int radius, int width, int height)
    {
        var (cq, cr) = OffsetToAxial(centerCol, centerRow);
        var result = new List<(int, int)>();
        // Iterate the axial bounding diamond, convert, bounds-check.
        for (int dq = -radius; dq <= radius; dq++)
        {
            int rLo = Mathf.Max(-radius, -dq - radius);
            int rHi = Mathf.Min(radius, -dq + radius);
            for (int dr = rLo; dr <= rHi; dr++)
            {
                var (col, row) = AxialToOffset(cq + dq, cr + dr);
                if (col >= 0 && row >= 0 && col < width && row < height)
                    result.Add((col, row));
            }
        }
        return result;
    }

    // ── render position (flat-top odd-q: odd columns nudged down) ────────
    /// <summary>World-space position of an offset tile for the strategic view's
    /// interlocking hex field. Odd columns shift down half a tile.</summary>
    public static Vector2 OffsetRenderPosition(int col, int row, float tilePx)
    {
        float x = col * tilePx;
        float y = row * tilePx + ((col & 1) == 1 ? tilePx * 0.5f : 0f);
        return new Vector2(x, y);
    }
}
