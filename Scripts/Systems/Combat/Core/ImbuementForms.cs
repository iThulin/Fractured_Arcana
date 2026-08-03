using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ImbuementForms.cs
//
// Purpose:        Builds the elemental SILHOUETTE for an imbued
//                 tile — flames, crystals, plates, ribbons — as one
//                 procedural shard mesh per element, from a single
//                 parameter table.
// Layer:          Tiles / VFX
// Collaborators:  ImbuementOverlay.cs (owns the MeshInstance3D),
//                 imbuement_aura.gdshader (shades what this builds),
//                 HexGridManager.PainterlyGrass.cs (the blade builder
//                 this borrows its cross-quad construction from)
// See:            docs/imbuement_painterly_redesign_v1.md §3.6, §3.7
// ============================================================
//
// WHAT THIS REPLACES, AND THE ARGUMENT AGAINST DOING IT
//
// Every imbued tile used to wear the same object: a tapered hex cylinder with
// noise scrolling inside it. It read as a translucent dome because that is
// what it was, and no amount of shader work fixes a silhouette.
//
// The counterargument to per-element shapes is real and should be stated
// before the table below: ONE shape guarantees ONE legibility profile. "Is
// this tile imbued at all?" is a cheaper and more frequent question than
// "which element is it?", and a shared silhouette answers it from any angle at
// any distance. Eight bespoke shapes means eight legibility profiles, and the
// worst one sets the floor for the whole system.
//
// Two things make it worth doing anyway:
//
//   1. It is ONE builder with a parameter table, not eight meshes. That is
//      redesign §3.7 applied to geometry rather than to shader branches. A
//      ninth element costs a row.
//   2. Every row is constrained to the same FOOTPRINT — shards are placed on
//      or inside the hex and rise from its surface — so the "something is on
//      this tile" read survives even where the "what" does not.
//
// The floor is set by the shortest form (Water, 0.20) seen at maximum camera
// distance. If that stops reading, raise its height before touching anything
// else, because it is what the whole system's legibility is bounded by.
//
// ── Construction ────────────────────────────────────────────────────────
// A form is N shards. A shard is two quads crossed at 90° along a leaning,
// curling spine — the same trick painterly grass uses to give a flat blade
// volume from any camera angle, and for the same reason.
//
// Mesh space: base at y = 0 (the tile's top surface), +Y up, tile radius 1.0.
// UV: u across the shard, v = 0 at base -> 1 at tip.
// COLOR.a: SOLIDITY at that vertex. This is what the shader fades with, which
// is why the shader no longer has to guess at a mesh's UV winding — the thing
// that authored the geometry says how solid it is, in the same place it says
// how tall it is.
// ============================================================

/// <summary>
/// Procedural elemental forms for imbued tiles. Eight meshes total for the whole
/// game, built on first use and cached forever.
/// </summary>
public static class ImbuementForms
{
    /// <summary>Where a form's shards are anchored on the hex.</summary>
    private enum Place
    {
        /// <summary>Evenly around a circle of the row's <c>radius</c>. Free count.</summary>
        Ring,
        /// <summary>The six hex EDGE midpoints, then a second ring inset. "Growing in from the edges."</summary>
        Edge,
        /// <summary>The six hex CORNERS. Reads as upheaval rather than growth.</summary>
        Corner,
        /// <summary>Jittered inside a small disc at the centre. Reads as a source.</summary>
        Cluster
    }

    private readonly struct Row
    {
        public readonly int Count;
        public readonly Place Where;
        public readonly float Radius;      // placement radius, tile radii
        public readonly float Height;      // world units above the tile top
        public readonly float HeightVary;  // 0..1 fraction of Height
        public readonly float Width;       // shard base half-width, tile radii
        public readonly float Taper;       // width falloff exponent; high = spike
        public readonly float TipFrac;     // width remaining at the tip, 0..1. 0 = point, high = plate
        public readonly float Bulge;       // mid-shard swell; high = flame
        public readonly float Tilt;        // degrees from vertical, + = outward, - = inward
        public readonly float Curl;        // extra progressive lean toward the tip
        public readonly float Twist;       // degrees of random yaw per shard
        public readonly float BaseSolid;   // COLOR.a at the base
        public readonly float TipSolid;    // COLOR.a at the tip
        public readonly float Lift;        // base raised off the ground, world units
        public readonly int   Cross;       // 1 = flat ribbon, 2 = quads crossed at 90 degrees

        public Row(int count, Place where, float radius, float height, float heightVary,
                   float width, float taper, float tipFrac, float bulge, float tilt, float curl,
                   float twist, int cross, float baseSolid, float tipSolid, float lift)
        {
            Count = count; Where = where; Radius = radius; Height = height;
            HeightVary = heightVary; Width = width; Taper = taper; TipFrac = tipFrac;
            Bulge = bulge; Tilt = tilt; Curl = curl; Twist = twist; Cross = cross;
            BaseSolid = baseSolid; TipSolid = tipSolid; Lift = lift;
        }
    }

    // ── The table ───────────────────────────────────────────────────
    //
    // Read the columns as a sentence. Fire: nine shards clustered at the
    // centre, tall, thin, swelling in the middle, curling inward, dissolving
    // at the tip. Frost: eleven shards on the hex edges, short, wide-based,
    // spiking, leaning HARD inward, solid all the way up.
    //
    //                              cnt  where          rad    hgt    vary   wid     taper tipfr bulge tilt  curl   twist X  base   tip    lift
    private static readonly Dictionary<TileElementType, Row> Table = new()
    {
        // Flames across the WHOLE tile, not a candle in the middle. Radius 0.72 with
        // heavy height variance so the fire has a ragged edge rather than a rim.
        [TileElementType.Fire]      = new(14, Place.Cluster, 0.72f, 0.86f, 0.52f, 0.115f, 1.35f, 0.00f, 0.85f,   7f, -0.34f, 30f, 2, 1.00f, 0.00f, 0.00f),

        // Crystals growing IN from the tile edges. The negative tilt is the single
        // most characterful number in the table. Taper 1.0 is deliberate: anything
        // above ~1.5 curves the profile and the spikes read as HORNS, which is the
        // first thing the preview render caught.
        [TileElementType.Frost]     = new(11, Place.Edge,    0.84f, 0.44f, 0.40f, 0.130f, 1.00f, 0.00f, 0.00f, -34f,  0.00f,  8f, 2, 1.00f, 0.88f, 0.00f),

        // AMETHYST CLUSTERS scattered over the whole tile, each a quartz-like
        // central spire with smaller shards crowding its base (see Satellites), and
        // lightning arcing between the spire TIPS (see ArcCount).
        //
        // Place.Cluster at 0.78 rather than a ring: a ring reads as something
        // ARRANGED, and mineral growth is not arranged. Base/tip solidity are 0.72
        // and 0.52 — these are the only GEMS in the table and they have to be
        // semi-translucent, which is also why they carry the strongest rim term in
        // element_look().
        [TileElementType.Lightning] = new(6,  Place.Cluster, 0.78f, 0.52f, 0.42f, 0.075f, 1.15f, 0.00f, 0.00f,   6f,  0.00f, 40f, 2, 0.72f, 0.52f, 0.00f),

        // Plates heaved up at the corners. TipFrac 0.72 is what makes these SLABS —
        // with a pointed tip they read as paddles or mushrooms, not stone.
        [TileElementType.Earth]     = new(6,  Place.Corner,  0.88f, 0.32f, 0.28f, 0.260f, 0.90f, 0.72f, 0.00f, -24f,  0.08f, 12f, 2, 1.00f, 0.95f, 0.00f),

        // A low rippling collar, flat ribbons rather than crossed shards — the
        // crossed version reads as a crown of teeth. Shortest form in the table and
        // therefore what bounds the whole system's legibility. See the header.
        [TileElementType.Water]     = new(10, Place.Ring,    0.60f, 0.20f, 0.22f, 0.300f, 0.70f, 0.45f, 0.30f, -10f,  0.20f,  8f, 1, 0.95f, 0.50f, 0.00f),

        // Orbiting ribbons. Heavy twist and a curl that reverses the outward tilt,
        // so each one arcs. The only form that reads as moving in a still frame.
        [TileElementType.Air]       = new(6,  Place.Ring,    0.55f, 0.55f, 0.35f, 0.100f, 1.10f, 0.03f, 0.75f,  24f, -0.85f, 60f, 1, 0.70f, 0.12f, 0.10f),

        // Shards floating clear of the ground. Lift is what sells it.
        [TileElementType.Arcane]    = new(7,  Place.Ring,    0.38f, 0.40f, 0.45f, 0.100f, 1.20f, 0.00f, 0.50f,  14f,  0.12f, 34f, 2, 0.85f, 0.55f, 0.20f),

        // Tendrils curling outward and down. Curl above 1.0 so the tips fall past
        // horizontal; low tip solidity so it looks like it is leaking off the tile.
        [TileElementType.Shadow]    = new(8,  Place.Ring,    0.42f, 0.46f, 0.40f, 0.150f, 1.20f, 0.02f, 0.45f,  26f,  1.05f, 34f, 1, 1.00f, 0.10f, 0.00f),
    };

    /// <summary>
    /// Smaller shards crowding the base of each main spire, per element. A lone spike is
    /// a spike; a spike with a litter of smaller ones around its foot is a mineral
    /// growth, and that difference is most of what makes amethyst read as amethyst.
    ///
    /// Satellites are NOT added to the arc tip list — lightning leaps between the tall
    /// spires, not the gravel.
    ///
    /// Separate map rather than Row columns for the same reason as ArcCount: four more
    /// constructor arguments that are zero in seven rows out of eight is not a table.
    /// </summary>
    private static readonly Dictionary<TileElementType, (int Count, float Scale, float Spread, float Tilt)> Satellites = new()
    {
        [TileElementType.Lightning] = (5, 0.34f, 0.12f, 24f),
    };

    /// <summary>
    /// Arcs drawn BETWEEN shard tips, per element. Lightning is the only entry and is
    /// likely to stay that way — this is a separate map rather than a Row column because
    /// a seventeenth constructor argument that is zero in seven rows out of eight is not
    /// a table, it is a wart.
    ///
    /// Arc vertices are flagged with COLOR.g = 1 so the shader can strobe them out of
    /// existence independently of the crystals, which stay solid.
    /// </summary>
    private static readonly Dictionary<TileElementType, int> ArcCount = new()
    {
        [TileElementType.Lightning] = 7,
    };

    /// <summary>Lengthwise segments per arc. Higher than a shard's: the jag IS the read.</summary>
    private const int ArcSegments = 9;

    /// <summary>Lengthwise segments per shard. Enough to make Curl read as a bend rather than a crease.</summary>
    private const int Segments = 6;

    private static readonly Dictionary<TileElementType, ArrayMesh> Cache = new();

    /// <summary>
    /// The form mesh for <paramref name="element"/>, built on first request and cached.
    /// Returns null for <see cref="TileElementType.None"/> and anything unmapped — callers
    /// must treat null as "keep the mesh you already had", never as "show nothing".
    /// </summary>
    public static ArrayMesh MeshFor(TileElementType element)
    {
        if (element == TileElementType.None) return null;
        if (Cache.TryGetValue(element, out var hit)) return hit;
        if (!Table.TryGetValue(element, out var row)) return null;

        var mesh = Build(element, row, GlyphCipher.Fnv1a32("imbuement_form:" + element));
        Cache[element] = mesh;
        return mesh;
    }

    /// <summary>Tallest point of a form, world units. The shader needs it to anchor the wind lean at the base.</summary>
    public static float HeightOf(TileElementType element)
        => Table.TryGetValue(element, out var r) ? r.Height * (1f + r.HeightVary) + r.Lift : 1f;

    /// <summary>Drops the built meshes. Only useful when hot-editing the table above.</summary>
    public static void ClearCache() => Cache.Clear();

    // ── Builder ─────────────────────────────────────────────────────

    private static ArrayMesh Build(TileElementType element, Row row, uint seed)
    {
        var rng = new Rng(seed);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        var tips = new List<Vector3>(row.Count);

        for (int i = 0; i < row.Count; i++)
        {
            GetAnchor(row, i, ref rng, out Vector3 basePos, out float outwardYaw);

            float yaw = outwardYaw + Mathf.DegToRad(row.Twist * (float)rng.Sym());
            float height = row.Height * (1f + row.HeightVary * (float)rng.Sym());
            basePos.Y += row.Lift;

            // Outward direction in XZ for this shard: tilt leans along it, so a
            // negative Tilt leans toward the tile centre. That is the whole
            // mechanic behind "crystals growing in from the edges".
            var outward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            var side = new Vector3(outward.Z, 0f, -outward.X);

            // The spire. Only these go in the tip list, so arcs leap spire-to-spire.
            tips.Add(AddShard(st, row, basePos, outward, side, height, Mathf.DegToRad(row.Tilt), ref rng, 1f));

            if (!Satellites.TryGetValue(element, out var sat)) continue;

            for (int k = 0; k < sat.Count; k++)
            {
                // Ring the spire's foot at an even bearing plus jitter, so the litter
                // surrounds it without ever landing in a visible pattern.
                float sa = Mathf.Tau * k / sat.Count + (float)rng.Sym() * 0.5f;
                var sOut = new Vector3(Mathf.Sin(sa), 0f, Mathf.Cos(sa));
                var sSide = new Vector3(sOut.Z, 0f, -sOut.X);
                Vector3 sBase = basePos + sOut * sat.Spread * (0.6f + (float)rng.Unit() * 0.7f);

                float sHeight = height * sat.Scale * (0.55f + (float)rng.Unit() * 0.55f);
                float sTilt = Mathf.DegToRad(sat.Tilt * (0.5f + (float)rng.Unit()));

                AddShard(st, row, sBase, sOut, sSide, sHeight, sTilt, ref rng, sat.Scale + 0.55f);
            }
        }

        if (ArcCount.TryGetValue(element, out int arcs) && tips.Count >= 2)
            AddArcs(st, tips, arcs, ref rng);

        st.GenerateNormals();
        return st.Commit();
    }

    private static void GetAnchor(Row row, int i, ref Rng rng, out Vector3 pos, out float yaw)
    {
        switch (row.Where)
        {
            case Place.Edge:
            {
                // The six edge midpoints first, then any surplus inset on a
                // second ring so a count above six thickens the fringe instead
                // of doubling shards on top of each other.
                int slot = i % 6;
                int band = i / 6;
                float a = Mathf.Tau * slot / 6f + (band == 0 ? 0f : Mathf.Tau / 12f);
                float r = row.Radius * (band == 0 ? 1f : 0.62f);
                yaw = a;
                pos = new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
                return;
            }
            case Place.Corner:
            {
                float a = Mathf.Tau * (i % 6) / 6f + Mathf.Tau / 12f;
                float r = row.Radius * (i < 6 ? 1f : 0.55f);
                yaw = a;
                pos = new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
                return;
            }
            case Place.Cluster:
            {
                // Golden-angle spiral with jitter, NOT uniform random.
                //
                // Uniform random over a disc is "correct" and looks wrong: six samples
                // reliably bunch on one side and leave half the tile bare, which reads
                // as a bug rather than as nature. The golden angle guarantees even
                // coverage; the jitter removes any trace of the spiral. Sunflowers use
                // it for exactly this reason.
                const float GoldenAngle = 2.39996323f;
                float a = i * GoldenAngle + (float)rng.Sym() * 0.40f;
                float r = row.Radius
                        * Mathf.Sqrt((i + 0.5f) / Mathf.Max(1, row.Count))
                        * (0.78f + (float)rng.Unit() * 0.36f);
                yaw = a;
                pos = new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
                return;
            }
            default:
            {
                float a = Mathf.Tau * i / Mathf.Max(1, row.Count);
                yaw = a;
                pos = new Vector3(Mathf.Sin(a) * row.Radius, 0f, Mathf.Cos(a) * row.Radius);
                return;
            }
        }
    }

    /// <summary>
    /// Two quads crossed at 90° along one spine. Borrowed wholesale from the painterly
    /// grass blade builder: a single quad vanishes to a line when the camera swings onto
    /// its edge, and an imbuement that disappears from some angles is a gameplay bug.
    /// </summary>
    private static Vector3 AddShard(SurfaceTool st, Row row, Vector3 basePos,
                                 Vector3 outward, Vector3 side, float height, float tilt,
                                 ref Rng rng, float widthMul)
    {
        var spine = new Vector3[Segments + 1];
        var width = new float[Segments + 1];
        var solid = new float[Segments + 1];

        float wobblePhase = (float)rng.Unit() * Mathf.Tau;
        float seed = (float)rng.Unit();

        for (int s = 0; s <= Segments; s++)
        {
            float t = (float)s / Segments;

            // Lean accumulates quadratically so the shard leaves the ground
            // upright and bends as it rises — a straight leaning stick reads as
            // fallen, not grown.
            float lean = tilt * t + row.Curl * t * t;
            float y = Mathf.Cos(lean) * height * t;
            float outAmount = Mathf.Sin(lean) * height * t;

            // A little hand-waver across the shard, so no two are identical and
            // nothing in the form is perfectly straight.
            float wob = Mathf.Sin(wobblePhase + t * 3.1f) * height * 0.035f;

            spine[s] = basePos + outward * outAmount + side * wob + Vector3.Up * y;
            // TipFrac keeps a plate a plate. Without it every profile collapses to a
            // point at the tip and Earth's slabs come out as paddles.
            float f = Mathf.Pow(1f - t, row.Taper);
            width[s] = row.Width * widthMul * (row.TipFrac + (1f - row.TipFrac) * f)
                                 * (1f + row.Bulge * Mathf.Sin(Mathf.Pi * t));
            solid[s] = Mathf.Lerp(row.BaseSolid, row.TipSolid, t);
        }

        AddRibbon(st, spine, width, solid, side, seed, 0f);
        // Cross == 1 is a genuine trade, not a saving: a flat ribbon foreshortens to a
        // line when the camera swings onto its edge. It is used only where the form is
        // MANY overlapping pieces (Water's collar, Air's orbit, Shadow's tendrils), so
        // some of them always face the camera and the read survives. Never use it on a
        // form with fewer than about six shards.
        if (row.Cross >= 2) AddRibbon(st, spine, width, solid, outward, seed, 0f);

        return spine[Segments];
    }

    private static void AddRibbon(SurfaceTool st, Vector3[] spine, float[] width, float[] solid,
                                  Vector3 across, float seed, float arc, int segments = Segments)
    {
        for (int s = 0; s < segments; s++)
        {
            Vector3 a0 = spine[s]     - across * width[s];
            Vector3 a1 = spine[s]     + across * width[s];
            Vector3 b0 = spine[s + 1] - across * width[s + 1];
            Vector3 b1 = spine[s + 1] + across * width[s + 1];

            float v0 = (float)s / segments;
            float v1 = (float)(s + 1) / segments;

            Vert(st, a0, new Vector2(0f, v0), solid[s], seed, arc);
            Vert(st, a1, new Vector2(1f, v0), solid[s], seed, arc);
            Vert(st, b1, new Vector2(1f, v1), solid[s + 1], seed, arc);

            Vert(st, a0, new Vector2(0f, v0), solid[s], seed, arc);
            Vert(st, b1, new Vector2(1f, v1), solid[s + 1], seed, arc);
            Vert(st, b0, new Vector2(0f, v1), solid[s + 1], seed, arc);
        }
    }

    /// <summary>
    /// Jagged ribbons leaping between crystal tips. Each arc gets its own seed, so they
    /// strobe independently and the tile never looks like one object blinking.
    ///
    /// Crossed quads despite being thin: an arc is the thing the player is meant to
    /// notice, and a flat ribbon vanishes entirely when the camera swings onto its edge.
    /// </summary>
    /// <summary>
    /// Best of two random candidates, by distance. Electricity jumps the shortest gap it
    /// can find, and visually a tile crossed by full-width arcs reads as a cage rather
    /// than as a discharge. Two samples is enough to bias short without making the
    /// pairing deterministic.
    /// </summary>
    private static int PickPartner(List<Vector3> tips, int i, int n, ref Rng rng)
    {
        int a = (i + 1 + (int)(rng.Unit() * (n - 1))) % n;
        int b = (i + 1 + (int)(rng.Unit() * (n - 1))) % n;
        if (a == i) return b;
        if (b == i) return a;
        return tips[i].DistanceSquaredTo(tips[a]) <= tips[i].DistanceSquaredTo(tips[b]) ? a : b;
    }

    private static void AddArcs(SurfaceTool st, List<Vector3> tips, int count, ref Rng rng)
    {
        int n = tips.Count;
        for (int a = 0; a < count; a++)
        {
            // Each arc starts at a different spire (so none is left out) and lands on
            // a nearby random one. A fixed stride made a polygon out of the arcs, which
            // is the "arranged" read the scattered placement exists to avoid; pure
            // random made half of them span the entire tile, which reads as a cage.
            int i = a % n;
            int j = PickPartner(tips, i, n, ref rng);
            if (i == j) continue;

            Vector3 p0 = tips[i], p1 = tips[j];
            Vector3 dir = p1 - p0;
            float len = dir.Length();
            if (len < 0.0001f) continue;

            Vector3 fwd = dir / len;
            Vector3 side = new Vector3(-fwd.Z, 0f, fwd.X).Normalized();

            float seed = (float)rng.Unit();
            float jag = len * 0.11f;

            var spine = new Vector3[ArcSegments + 1];
            var width = new float[ArcSegments + 1];
            var solid = new float[ArcSegments + 1];

            for (int s = 0; s <= ArcSegments; s++)
            {
                float t = (float)s / ArcSegments;

                // Endpoints EXACT so the arc lands on the crystal and not near it.
                // Same rule the cipher's strokes follow, and for the same reason: a
                // connection that misses reads as a bug, not as energy.
                float k = (s == 0 || s == ArcSegments) ? 0f : 1f;

                // A bow upward plus a lateral zigzag. The sin() term keeps the
                // displacement zero at both ends without needing a special case.
                float bow = Mathf.Sin(Mathf.Pi * t) * len * 0.055f;

                spine[s] = p0 + dir * t
                         + Vector3.Up * bow
                         + side * ((float)rng.Sym() * jag * k)
                         + Vector3.Up * ((float)rng.Sym() * jag * 0.5f * k);

                // Thin. A bolt is a LINE — the first pass used 0.030 and the arcs
                // read as a fence strung between the crystals rather than as energy.
                width[s] = 0.013f * (0.35f + Mathf.Sin(Mathf.Pi * t));
                solid[s] = 1f;
            }

            AddRibbon(st, spine, width, solid, side, seed, 1f, ArcSegments);
            AddRibbon(st, spine, width, solid, Vector3.Up, seed, 1f, ArcSegments);
        }
    }

    private static void Vert(SurfaceTool st, Vector3 p, Vector2 uv, float solid, float seed, float arc)
    {
        // r = PER-SHARD SEED, g = ARC FLAG (0 = body, 1 = arc), a = solidity. b spare.
        //
        // The seed is what lets nine flames flicker on nine different beats while
        // still riding one gust. This is a phase offset, not a second clock — the
        // tempo still comes from element_motion()'s rate column, which is the
        // distinction the whole table rests on. Without it, a "flicker" is the
        // entire form pulsing in unison, which reads as a heartbeat.
        st.SetColor(new Color(seed, arc, 1f, solid));
        st.SetUV(uv);
        st.AddVertex(p);
    }

    // Same xorshift32 as GlyphCipher's, for the same reason ElementRunes copies it:
    // that type is private there and its draw order is pinned by 42 golden checksums.
    // Separate stream, separate seed namespace, zero risk to the goldens.
    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }
        public uint NextU32()
        {
            uint x = _s;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _s = x; return x;
        }
        /// <summary>[0,1)</summary>
        public double Unit() => NextU32() / 4294967296.0;
        /// <summary>[-1,1)</summary>
        public double Sym() => Unit() * 2.0 - 1.0;
    }
}
