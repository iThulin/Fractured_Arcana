using Godot;
using System.Collections.Generic;

// ============================================================
// ImbuementRocks.cs
//
// Purpose:        Earth imbuement as ACTUAL ROCKS — boulders from the
//                 map's own rock pool, uprooted and set onto the tile,
//                 instead of shader-drawn slabs.
// Layer:          Tiles / Terrain
// Collaborators:  ImbuementOverlay.cs (owns the MultiMeshInstance3D),
//                 HexGridManager.Rocks.cs (the pool, material and tuning
//                 this reads), painterly_rock.gdshader
// See:            docs/imbuement_painterly_redesign_v1.md §3.6
// ============================================================
//
// WHY EARTH LEAVES THE SHARD BUILDER
//
// Every other element is a transient. Fire burns out, frost melts, an arc is
// gone in a frame. ImbuementForms builds soft, translucent, wind-aware shards —
// a vocabulary for things passing through — and Earth's slabs were the weakest
// row in that table precisely because they were fighting it. Stone's whole
// character is that it STAYS.
//
// So Earth stops being drawn and starts being PLACED, using the same meshes and
// material the map already scatters on its stone and mountain tiles.
//
// ── Read from the LIVE GRID, not from paths ─────────────────────────────
//
// The first version of this file hardcoded six res:// boulder paths and one
// material. That was wrong twice over:
//
//   1. The scale was invented. HexGridManager's live tuning is
//      RockScale 0.55 x RockStoneScaleMult 1.7 = ~0.94 for a stone-tile
//      boulder; the hardcoded 0.34 was nearly three times too small, which is
//      why the rocks were barely visible.
//   2. It could never follow the map. Everything about the rock look —
//      which meshes, which material, how big, how far they sink, how much they
//      tilt — is configured per scene on HexGridManager, and a second copy of
//      those numbers is a second thing to keep in sync.
//
// So this now reads the grid's own exports and derives everything from them. An
// imbued tile's boulders are the SAME boulders, at the SAME scale, in the SAME
// material as the ones the map scatters on its own stone tiles — by
// construction rather than by matching numbers.
//
// ── On "the right biome" ────────────────────────────────────────────────
//
// Worth being straight about what exists: there is no per-biome rock shader in
// this project today. There are exactly TWO rock looks, both static exports on
// HexGridManager — RockMaterial (the cool mossy meadow scree) and
// MountainRockMaterial (the warm dry boulder palette) — and the map recipes do
// not swap either. Themes tint the terrain splat, not the rocks.
//
// Earth takes the MOUNTAIN pair, and that is a deliberate reading rather than a
// default: an imbued tile is bedrock heaved up through whatever is growing on
// top of it, so it should look like the mountain, not like the meadow it came
// up through. Rock_1..9 are pebbles and would read as gravel.
//
// If per-biome rock materials are ever added, this file needs no change — it
// asks the grid, and the grid will have the answer.
//
// ── Uprooting ───────────────────────────────────────────────────────────
//
// A stone that is simply THERE reads as scenery. The thing that says an
// imbuement happened is the ground being disturbed, so the scatter ships two
// extra pieces:
//
//   DEBRIS — the same rock meshes, squashed flat and tinted earth-brown through
//   INSTANCE_CUSTOM. painterly_rock.gdshader already supports a per-instance
//   palette tint (`use_instance_tint`), so churned soil costs a second MultiMesh
//   and no new art at all.
//
//   The squash has a FLOOR, and it is not an aesthetic one. The grass on this
//   map stands about 0.3 world units; anything shorter than that is not subtle,
//   it is absent. The first pass squashed to 18% — roughly 0.05 units — and then
//   sank it, which put the soil underground before the grass even got involved.
//
//   RISE — each stone starts fully buried and is driven up on its own seeded
//   delay. Staggered, because seven stones surfacing in unison reads as a
//   platform being raised, not as ground breaking. The per-instance transforms
//   are rewritten each frame for the ~1s of the animation; at seven instances
//   that is nothing, and it avoids touching the shared rock shader, which is
//   the one thing here that must not become imbuement-aware.
//
// ── The honest cost ─────────────────────────────────────────────────────
// Earth now looks nothing like the other seven elements, and that is a real
// loss: the shard grammar is what makes the set cohere. It is paid for by stone
// being the one element where "this is permanent, it is part of the terrain
// now" is the correct read, and by the element RUNE still floating above it in
// the same hand as every other element's. The rune keeps Earth in the family.
// ============================================================

/// <summary>
/// Builds the boulder scatter for prop-based imbuements. One <see cref="MultiMesh"/> per
/// tile, one boulder variant per tile; variety across the board comes from tile-to-tile
/// variant choice plus per-instance transform.
/// </summary>
public static class ImbuementRocks
{
    /// <summary>
    /// Fallback pool, used ONLY when no HexGridManager can be reached (a tile previewed
    /// outside a built map, a test scene). The live path reads the grid's own exports.
    /// </summary>
    private static readonly string[] FallbackBoulderPaths =
    {
        "res://Assets/Props/Rocks/Boulder_Craggy.obj",
        "res://Assets/Props/Rocks/Boulder_Low.obj",
        "res://Assets/Props/Rocks/Boulder_Round.obj",
        "res://Assets/Props/Rocks/Boulder_Slab.obj",
        "res://Assets/Props/Rocks/Boulder_Spire.obj",
        "res://Assets/Props/Rocks/Boulder_Wedge.obj",
    };

    private const string FallbackMaterialPath =
        "res://Assets/Materials/PainterlyMaterials/Painterly_rocks_mountain.tres";

    private readonly struct Row
    {
        public readonly int Count;
        public readonly float Radius;    // scatter radius, tile radii
        public readonly float ScaleMul;  // multiplies the grid's own stone-tile boulder scale
        public readonly float SinkMul;   // multiplies the grid's RockSinkDepth
        public readonly float TiltMul;   // multiplies the grid's RockTiltJitter
        public readonly int   Variants;  // distinct meshes drawn from the pool per tile
        public readonly int   Debris;    // dirt clods per stone
        public readonly float DebrisFlat;// vertical squash applied to a clod

        public Row(int count, float radius, float scaleMul, float sinkMul, float tiltMul,
                   int variants, int debris, float debrisFlat)
        { Count = count; Radius = radius; ScaleMul = scaleMul; SinkMul = sinkMul; TiltMul = tiltMul;
          Variants = variants; Debris = debris; DebrisFlat = debrisFlat; }
    }

    // Everything here is a MULTIPLIER on the grid's own numbers, never an absolute.
    // That is the whole point: retune the map's rocks and the imbuement follows.
    //
    // ScaleMul sits slightly BELOW the ambient stone-tile scatter, not above. The
    // first tuning went the other way and the result read as one lumpy mass filling
    // the tile rather than as several stones: a landmark is made by COUNT and SPREAD,
    // and a cluster of distinguishable stones says "placed here" where one blob says
    // "a boulder happens to be here".
    //
    // Variants is why the previous pass looked stamped: every instance shared one mesh
    // and seven copies of the same dome, however rotated, reads as repetition rather
    // than as rubble.
    private static readonly Dictionary<TileElementType, Row> Table = new()
    {
        [TileElementType.Earth] = new(7, 0.72f, 0.80f, 1.10f, 1.00f, 3, 5, 0.42f),
    };

    /// <summary>Earth-brown the debris clods are tinted toward, through INSTANCE_CUSTOM.</summary>
    private static readonly Color DirtTint = new(0.30f, 0.22f, 0.15f);

    /// <summary>
    /// Apothem of a unit hex — the inradius, not the circumradius. Placement is in tile
    /// radii, so 1.0 is a CORNER and anything past 0.866 in the wrong direction is over
    /// the edge.
    /// </summary>
    private const float Apothem = 0.8660254f;

    /// <summary>Extra margin inside the apothem. Rock silhouettes are not their bounding boxes, so this is deliberately conservative.</summary>
    private const float HexMargin = 0.90f;

    private static Material _fallbackMaterial;
    private static Mesh[] _fallbackBoulders;

    /// <summary>True when this element is built from props rather than from ImbuementForms' shards.</summary>
    public static bool HasRockForm(TileElementType element) => Table.ContainsKey(element);

    /// <summary>
    /// Walks up from <paramref name="from"/> to the grid that owns it. Tiles are direct
    /// children of HexGridManager (HexGridManager.Generation.cs: <c>AddChild(tileNode)</c>),
    /// so this is two hops from an overlay — but it walks rather than assuming, because a
    /// wrong assumption here fails silently as "no rocks".
    /// </summary>
    public static HexGridManager FindGrid(Node from)
    {
        for (Node n = from; n != null; n = n.GetParent())
            if (n is HexGridManager g) return g;
        return null;
    }

    /// <summary>
    /// The material an imbued tile's rocks should wear. Prefers the grid's mountain
    /// palette, falls back to its meadow one, then to the packaged resource. Null means
    /// the caller must skip — never render material-less, the same rule
    /// HexGridManager.Rocks follows.
    /// </summary>
    public static Material MaterialFor(HexGridManager grid)
    {
        if (grid?.MountainRockMaterial != null) return grid.MountainRockMaterial;
        if (grid?.RockMaterial != null) return grid.RockMaterial;

        if (_fallbackMaterial == null)
            _fallbackMaterial = GD.Load<Material>(FallbackMaterialPath);
        return _fallbackMaterial;
    }

    /// <summary>
    /// Scatter for one tile. <paramref name="seed"/> must be stable per tile (the node's
    /// instance id works) so a tile's rocks do not reshuffle every time it is re-imbued —
    /// permanence is the whole point of the element.
    /// </summary>
    public static RockScatter Build(TileElementType element, ulong seed, HexGridManager grid)
    {
        if (!Table.TryGetValue(element, out var row)) return null;

        var pool = PoolFor(grid);
        if (pool == null || pool.Length == 0) return null;

        var rng = new Rng((uint)(seed ^ (seed >> 32)) ^ GlyphCipher.Fnv1a32("imbue_rock:" + element));

        // Match the map's own stone-tile boulders by construction. HexGridManager.Rocks
        // multiplies RockScale by RockStoneScaleMult on stone/mountain tiles; an imbued
        // tile IS stone now, so it gets the same treatment.
        float baseScale = (grid?.RockScale ?? 0.55f) * (grid?.RockStoneScaleMult ?? 1.7f) * row.ScaleMul;
        float jitter    = grid?.RockScaleJitter ?? 0.45f;
        float sink      = (grid?.RockSinkDepth ?? 0.05f) * row.SinkMul;
        float tiltMax   = (grid?.RockTiltJitter ?? 0.30f) * row.TiltMul;

        // Placement columns in the table are TILE RADII; boulder scale is world units
        // (it comes from the grid already). This project runs at HexRadius 1.325, so
        // treating "1 tile radius" as 1 world unit shrank the whole scatter — and the
        // hex clamp with it — to about three quarters of the tile.
        float tileR = (grid?.HexRadius ?? 1f);

        // Draw a few DISTINCT meshes for this tile rather than one. Sampling without
        // replacement matters: a random pick per instance repeats at these counts often
        // enough to be noticeable, which is the failure this replaces.
        int variantCount = Mathf.Min(row.Variants, pool.Length);
        var chosen = new Mesh[variantCount];
        var taken = new List<int>(pool.Length);
        for (int i = 0; i < pool.Length; i++) taken.Add(i);
        for (int v = 0; v < variantCount; v++)
        {
            int k = (int)(rng.Unit() * taken.Count) % taken.Count;
            chosen[v] = pool[taken[k]];
            taken.RemoveAt(k);
        }

        var stoneTf = new List<Transform3D>[variantCount];
        var stoneRise = new List<Vector2>[variantCount];   // x = depth, y = delay
        var dirtTf = new List<Transform3D>[variantCount];
        var dirtDelay = new List<float>[variantCount];
        for (int v = 0; v < variantCount; v++)
        {
            stoneTf[v] = new List<Transform3D>();
            stoneRise[v] = new List<Vector2>();
            dirtTf[v] = new List<Transform3D>();
            dirtDelay[v] = new List<float>();
        }

        float topY = 0f;

        for (int i = 0; i < row.Count; i++)
        {
            // Golden-angle placement, same as the shard forms and for the same reason:
            // uniform random bunches at these counts and leaves half the tile bare.
            const float GoldenAngle = 2.39996323f;
            float a = i * GoldenAngle + (float)rng.Sym() * 0.45f;
            float r = row.Radius * tileR * Mathf.Sqrt((i + 0.5f) / row.Count) * (0.72f + (float)rng.Unit() * 0.45f);

            var pos = new Vector3(Mathf.Sin(a) * r, -sink, Mathf.Cos(a) * r);

            float s = baseScale * (1f + jitter * (float)rng.Sym());
            float yaw = Mathf.Tau * (float)rng.Unit();
            float tiltDir = Mathf.Tau * (float)rng.Unit();
            float tiltA = tiltMax * (float)rng.Unit();

            var basis = new Basis(Vector3.Up, yaw);
            var tiltAxis = new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir));
            basis = new Basis(tiltAxis, tiltA) * basis;
            basis = basis.Scaled(new Vector3(s, s, s));

            int variant = i % variantCount;
            var aabb = chosen[variant].GetAabb();
            float height = Mathf.Max(0.05f, aabb.Size.Y * s);

            // Keep the stone on its own tile, allowing for its own width.
            float halfW = Mathf.Max(aabb.Size.X, aabb.Size.Z) * 0.5f * s;
            pos = ClampToTile(pos, halfW, tileR);

            stoneTf[variant].Add(new Transform3D(basis, pos));
            // Start fully buried, plus a little, so nothing is peeking before it moves.
            stoneRise[variant].Add(new Vector2(height * 1.15f, (float)rng.Unit() * RiseSpread));

            // True top of this stone, from the MESH's own bounds. Measured rather than
            // estimated because the rune has to clear it, and a guessed clearance is a
            // rune buried in the rubble on any pool the estimate was not written for.
            topY = Mathf.Max(topY, pos.Y + aabb.End.Y * s);

            // Clods thrown up around the base. Same mesh, crushed flat.
            //
            // The first tuning made these invisible and the arithmetic says why: at
            // 18% squash a clod stood about 0.05 world units tall and was then SUNK
            // below the surface, so it was underground before the grass — which is
            // ~0.3 tall — got a chance to hide it. A clod has to clear the turf or it
            // is not a clod, it is a buried rock.
            //
            // So: squashed to 42% rather than 18%, wider, ringed further out so the
            // stone does not sit on top of its own spoil, and resting ON the surface
            // rather than in it.
            for (int d = 0; d < row.Debris; d++)
            {
                float da = Mathf.Tau * (float)rng.Unit();
                float dr = s * (0.50f + (float)rng.Unit() * 0.55f);
                float ds = s * (0.34f + (float)rng.Unit() * 0.40f);

                var dpos = ClampToTile(new Vector3(pos.X + Mathf.Sin(da) * dr,
                                                   -sink * 0.25f,
                                                   pos.Z + Mathf.Cos(da) * dr),
                                       Mathf.Max(aabb.Size.X, aabb.Size.Z) * 0.5f * ds, tileR);

                var dbasis = new Basis(Vector3.Up, Mathf.Tau * (float)rng.Unit())
                    .Scaled(new Vector3(ds, ds * row.DebrisFlat, ds));

                dirtTf[variant].Add(new Transform3D(dbasis, dpos));
                // Soil is thrown clear at the LAUNCH — the instant the stone stops
                // straining and breaks through — not partway up. That beat is at
                // ImbuementOverlay.RockRumbleFraction into the stone's own window;
                // 0.34 tracks its default. Off by a little is fine, off by a lot
                // reads as the dirt appearing for no reason.
                dirtDelay[variant].Add(stoneRise[variant][^1].Y + 0.34f);
            }
        }

        var res = new RockScatter { TopY = topY };
        var stones = new List<MultiMesh>();
        var debris = new List<MultiMesh>();
        var sFinal = new List<Transform3D[]>();
        var sRise = new List<Vector2[]>();
        var dFinal = new List<Transform3D[]>();
        var dDelay = new List<float[]>();

        for (int v = 0; v < variantCount; v++)
        {
            if (stoneTf[v].Count == 0) continue;

            stones.Add(MakeMulti(chosen[v], stoneTf[v], null));
            sFinal.Add(stoneTf[v].ToArray());
            sRise.Add(stoneRise[v].ToArray());

            if (dirtTf[v].Count > 0)
            {
                debris.Add(MakeMulti(chosen[v], dirtTf[v], DirtTint));
                dFinal.Add(dirtTf[v].ToArray());
                dDelay.Add(dirtDelay[v].ToArray());
            }
        }

        if (stones.Count == 0) return null;

        res.Stones = stones.ToArray();
        res.StoneFinal = sFinal.ToArray();
        res.StoneRise = sRise.ToArray();
        res.Debris = debris.ToArray();
        res.DebrisFinal = dFinal.ToArray();
        res.DebrisDelay = dDelay.ToArray();
        return res;
    }

    /// <summary>Longest possible (delay + travel) in normalised animation units. The caller runs the clock; this says how far it has to go.</summary>
    public const float RiseSpread = 0.45f;

    /// <summary>
    /// Pulls a placement back inside the hex, allowing for how wide the thing standing
    /// there is.
    ///
    /// Necessary because a tile can sit HIGHER than its neighbours: anything overhanging
    /// the rim is then hanging in mid-air above the tile next door, which is exactly what
    /// it looks like. On a flat board the overhang just blended into the grass and nobody
    /// noticed — the bug was always there, terrain height only made it visible.
    /// </summary>
    private static Vector3 ClampToTile(Vector3 p, float ownRadius, float tileRadius)
    {
        float lim = Mathf.Max(0.05f, Apothem * HexMargin * tileRadius - ownRadius);
        var xz = new Vector2(p.X, p.Z);
        if (xz.Length() <= lim) return p;
        xz = xz.Normalized() * lim;
        return new Vector3(xz.X, p.Y, xz.Y);
    }

    private static MultiMesh MakeMulti(Mesh mesh, List<Transform3D> tf, Color? tint)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            // painterly_rock.gdshader reads INSTANCE_CUSTOM.rgb as a palette tint when
            // it is non-zero and falls back to `fallback_tint` otherwise
            // (`dot(INSTANCE_CUSTOM.rgb, vec3(1.0)) > 0.0`). Stones want the fallback;
            // debris wants the dirt colour. Same shader, same material, no branch.
            UseCustomData = tint.HasValue,
            Mesh = mesh,
            InstanceCount = tf.Count,
        };
        for (int i = 0; i < tf.Count; i++)
        {
            mm.SetInstanceTransform(i, tf[i]);
            if (tint.HasValue) mm.SetInstanceCustomData(i, tint.Value);
        }
        return mm;
    }

    /// <summary>Widest reach of a form in tile radii, for the visibility AABB.</summary>
    public static float ExtentOf(TileElementType element, HexGridManager grid)
    {
        if (!Table.TryGetValue(element, out var r)) return 1f;
        float boulder = (grid?.RockScale ?? 0.55f) * (grid?.RockStoneScaleMult ?? 1.7f) * r.ScaleMul;
        return r.Radius * (grid?.HexRadius ?? 1f) + boulder * 1.5f;
    }

    /// <summary>
    /// Boulder meshes, preferring the grid's MOUNTAIN pool. An imbued tile is bedrock
    /// heaved up through the turf, so it wants boulders; the meadow pool (Rock_1..9) is
    /// scree and would read as gravel scattered on the grass.
    /// </summary>
    private static Mesh[] PoolFor(HexGridManager grid)
    {
        if (grid?.MountainRockMeshes != null && grid.MountainRockMeshes.Length > 0)
            return grid.MountainRockMeshes;
        if (grid?.RockMeshes != null && grid.RockMeshes.Length > 0)
            return grid.RockMeshes;

        if (_fallbackBoulders != null) return _fallbackBoulders;

        var found = new List<Mesh>(FallbackBoulderPaths.Length);
        foreach (var path in FallbackBoulderPaths)
        {
            var m = GD.Load<Mesh>(path);
            if (m != null) found.Add(m);
            else GD.PushWarning($"[ImbuementRocks] Missing fallback boulder '{path}' — skipped.");
        }
        _fallbackBoulders = found.ToArray();

        if (_fallbackBoulders.Length == 0)
            GD.PushWarning("[ImbuementRocks] No boulder meshes available — Earth falls back to the shard form.");

        return _fallbackBoulders;
    }

    // Same xorshift32 as GlyphCipher's, copied for the same reason ElementRunes and
    // ImbuementForms copy it: that type is private there and its draw order is pinned
    // by 42 golden checksums. Separate stream, separate seed namespace.
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

/// <summary>
/// One tile's worth of uprooted stone: the boulders, the soil thrown up around them, and
/// everything the caller needs to drive the rise. Split out of the builder so the
/// animation state lives with the geometry that defines it rather than being
/// reconstructed by whoever is playing it back.
/// </summary>
public sealed class RockScatter
{
    /// <summary>One MultiMesh per distinct boulder mesh.</summary>
    public MultiMesh[] Stones;
    /// <summary>Resting transforms, parallel to <see cref="Stones"/>.</summary>
    public Transform3D[][] StoneFinal;
    /// <summary>Per instance: x = how far below the surface it starts, y = its delay.</summary>
    public Vector2[][] StoneRise;

    /// <summary>Dirt clods. May be shorter than <see cref="Stones"/> if a variant had none.</summary>
    public MultiMesh[] Debris;
    /// <summary>Resting transforms, parallel to <see cref="Debris"/>.</summary>
    public Transform3D[][] DebrisFinal;
    /// <summary>Per instance delay before the clod scales in.</summary>
    public float[][] DebrisDelay;

    /// <summary>Measured top of the tallest stone, mesh space. The rune has to clear it.</summary>
    public float TopY;
}
