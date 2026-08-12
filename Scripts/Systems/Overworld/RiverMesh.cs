using Godot;
using System.Collections.Generic;

// ============================================================
// RiverMesh.cs — art pass A9b (2026-08-12)
//
// Purpose:        Winding river RIBBON geometry for the 3D world
//                 renderers, replacing the straight centre→edge box
//                 strokes ("subway map" read). Shared by WorldAtlas3D
//                 and ExpeditionWindow3D (one home, no drift).
//
// The model:      per tile —
//                 · 2 river edges  → ONE quadratic Bézier from edge
//                   midpoint to edge midpoint, control at the tile
//                   centre: the river BENDS through the tile.
//                 · 1 edge (source/mouth) → a tapering spoke from the
//                   centre out (rivers are born thin).
//                 · 3+ edges (confluence) → spokes to the centre
//                   (rivers legitimately join at angles).
//                 A deterministic meander offsets each path
//                 perpendicular to its tangent. The envelope
//                 16·t²(1−t)² has zero VALUE and zero SLOPE at both
//                 ends, and endpoint tangents lie along the
//                 centre→edge-midpoint line — which is collinear with
//                 the neighbour's — so curves meet across tile
//                 boundaries with C1 continuity by construction.
//
// Geometry:       3-vertex cross-sections (bank / waterline / bank),
//                 vertex colours darkening toward the banks for the
//                 recessed-channel read, +Y normals, CLOCKWISE
//                 winding (Godot front faces are CW seen from the
//                 front — the painterly-water session gotcha; if
//                 rivers ever render invisible from above, the tri
//                 order in Quad() is the suspect).
// ============================================================

/// <summary>Builds one merged ArrayMesh of winding river ribbons from per-tile
/// centre + active-edge-midpoint data (all points carry their tile's Y).</summary>
public static class RiverMesh
{
    /// <summary>Build the world's rivers as one mesh. <paramref name="width"/> is
    /// the base ribbon width in world units; colour runs body at the waterline to
    /// bank at the edges.</summary>
    public static ArrayMesh Build(List<(Vector3 center, List<Vector3> mids)> tiles,
                                  float width, Color body, Color bank)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var (center, mids) in tiles)
        {
            if (mids.Count == 2)
            {
                Ribbon(st, Path(mids[0], center, mids[1], 14), width, body, bank, taperStart: false);
            }
            else
            {
                foreach (var m in mids)
                    Ribbon(st, Path(center, (center + m) * 0.5f, m, 8), width, body, bank,
                           taperStart: mids.Count == 1);
            }
        }
        return st.Commit();
    }

    /// <summary>Sample a quadratic Bézier a→b (control c) and add the meander:
    /// perpendicular offset, deterministic from the endpoints' world position.</summary>
    private static List<Vector3> Path(Vector3 a, Vector3 c, Vector3 b, int segs)
    {
        uint h = HashV(a + b);
        float amp = 0.10f + F01(h) * 0.12f;
        float freq = 0.8f + F01(h * 2654435761u) * 0.5f;
        float ph = F01(h ^ 0x9E3779B9u);
        float ph2 = F01(h * 40503u);

        var pts = new List<Vector3>(segs + 1);
        for (int k = 0; k <= segs; k++)
        {
            float t = (float)k / segs;
            float u = 1f - t;
            Vector3 p = u * u * a + 2f * u * t * c + t * t * b;
            Vector3 tan = 2f * u * (c - a) + 2f * t * (b - c);
            Vector3 n = new Vector3(-tan.Z, 0f, tan.X);
            n = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Right;
            float env = 16f * t * t * u * u;   // zero value AND slope at both ends
            float meander = Mathf.Sin(Mathf.Tau * (freq * t + ph)) * 0.7f
                          + Mathf.Sin(Mathf.Tau * (2f * freq * t + ph2)) * 0.3f;
            pts.Add(p + n * (env * amp * meander));
        }
        return pts;
    }

    /// <summary>Triangulate a path into a flat ribbon: bank / waterline / bank
    /// cross-sections, width breathing gently along the run.</summary>
    private static void Ribbon(SurfaceTool st, List<Vector3> pts, float width,
                               Color body, Color bank, bool taperStart)
    {
        int n = pts.Count;
        if (n < 2) return;
        uint wh = HashV(pts[0] + pts[n - 1] * 3f);
        float wph = F01(wh);

        var left = new Vector3[n];
        var mid = new Vector3[n];
        var right = new Vector3[n];
        for (int k = 0; k < n; k++)
        {
            Vector3 tan = k == 0 ? pts[1] - pts[0]
                        : k == n - 1 ? pts[n - 1] - pts[n - 2]
                        : pts[k + 1] - pts[k - 1];
            Vector3 nor = new Vector3(-tan.Z, 0f, tan.X);
            nor = nor.LengthSquared() > 1e-8f ? nor.Normalized() : Vector3.Right;
            float t = (float)k / (n - 1);
            float w = width * (0.85f + 0.15f * Mathf.Sin(Mathf.Tau * (1.7f * t + wph)));
            if (taperStart)
                w *= Mathf.Lerp(0.35f, 1f, t);   // a source is born thin
            left[k] = pts[k] - nor * (w * 0.5f);
            mid[k] = pts[k];
            right[k] = pts[k] + nor * (w * 0.5f);
        }
        for (int k = 0; k + 1 < n; k++)
        {
            Quad(st, left[k], left[k + 1], mid[k + 1], mid[k], bank, body);
            Quad(st, mid[k], mid[k + 1], right[k + 1], right[k], body, bank);
        }
    }

    /// <summary>Two triangles; edge a0–a1 coloured ca, edge b0–b1 coloured cb.
    /// Vertex order is CW seen from +Y (Godot front-face rule).</summary>
    private static void Quad(SurfaceTool st, Vector3 a0, Vector3 a1, Vector3 b1, Vector3 b0,
                             Color ca, Color cb)
    {
        AddV(st, a0, ca); AddV(st, a1, ca); AddV(st, b1, cb);
        AddV(st, a0, ca); AddV(st, b1, cb); AddV(st, b0, cb);
    }

    private static void AddV(SurfaceTool st, Vector3 p, Color c)
    {
        st.SetColor(c);
        st.SetNormal(Vector3.Up);
        st.AddVertex(p);
    }

    private static uint HashV(Vector3 v)
    {
        int x = Mathf.RoundToInt(v.X * 37f), z = Mathf.RoundToInt(v.Z * 37f);
        uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return h;
    }

    private static float F01(uint h) => (h & 0xFFFFu) / 65535f;
}
