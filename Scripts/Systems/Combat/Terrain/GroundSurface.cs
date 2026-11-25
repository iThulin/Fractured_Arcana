using Godot;
using System;

// ============================================================
// GroundSurface.cs
//
// Purpose:        The ground as ONE continuous surface. A single height
//                 function, Sample(x, z), defines the terrain everywhere
//                 on the board. Every tile's mesh is a patch cut from that
//                 one surface, with normals taken from the surface itself,
//                 so neighbouring tiles agree exactly along shared edges:
//                 no seams, no lighting creases, and no vertical wall
//                 faces hung between tiles.
// Layer:          System (combat terrain, geometry only, no rules)
// Collaborators:  HexMeshBuilder (Build and SampleSurfaceWorldY hand over
//                 to this class when grid.ContinuousGroundActive),
//                 HexGridManager.Ground.cs (the tuning exports),
//                 terrain_splat.gdshader (reads the same vertex layout
//                 as the old builder: COLOR = splat weights, CUSTOM0 =
//                 splat layer indices, COLOR.a = 1 on map-edge skirts)
// See:            docs/continuous_ground_v1.md
//
// How the height works:
//   Each tile pulls the ground toward its own height with a weight that
//   is 1 near its centre and falls smoothly to 0 at GroundBlendRadius
//   (in units of the centre-to-centre spacing). The ground at a point is
//   the weighted mean of the tiles in reach, plus each terrain's own
//   rolling noise blended the same way. Near a tile's centre no neighbour
//   has any weight, so the tile is level there and units stand flat.
//
// How cliffs work:
//   Nothing is special-cased. Where the tiles in reach differ by more
//   than the cliff threshold, the weights are sharpened so the change of
//   height is squeezed into a narrow band at the hex edge: a steep rock
//   face, still one continuous surface. Movement rules are untouched
//   (HexGridManager.CliffHeightThreshold still decides what blocks).
//
// Not reentrant: the scratch arrays below are shared, and mesh building
// runs on the main thread only.
// ============================================================

public static class GroundSurface
{
    private const float Sqrt3 = 1.7320508f;
    private const int MaxNear = 7;          // the nearest tile and its six neighbours
    private const int MaxLayers = 4;        // distinct terrains kept per mesh point
    private const float NormalStep = 0.06f; // world units, for the surface slope
    private const float SkirtFloorDarkening = 0.5f;

    private static readonly TileData[] _nearTile = new TileData[MaxNear];
    private static readonly float[] _nearReach = new float[MaxNear];   // raw falloff, 0..1
    private static readonly float[] _nearWeight = new float[MaxNear];  // after cliff sharpening

    private static HexGridManager _originGrid;   // whose position _origin holds
    private static Vector3 _origin;

    // ────────────────────────────────────────────────────────
    // Height
    // ────────────────────────────────────────────────────────

    /// <summary>World Y of the ground at a world XZ. 0 where no tile is in reach.</summary>
    public static float Sample(HexGridManager grid, float worldX, float worldZ)
    {
        int n = Gather(grid, worldX, worldZ);
        return n > 0 ? HeightFromNear(grid, n, worldX, worldZ) : 0f;
    }

    /// <summary>
    /// Unit normal of the ground at a world XZ, from the slope of the height
    /// function itself. It depends on the world position only, so two tiles that
    /// share a point get the identical normal and light the same across the edge.
    /// </summary>
    public static Vector3 Normal(HexGridManager grid, float worldX, float worldZ, float worldY)
    {
        int nx = Gather(grid, worldX + NormalStep, worldZ);
        float yx = nx > 0 ? HeightFromNear(grid, nx, worldX + NormalStep, worldZ) : worldY;
        int nz = Gather(grid, worldX, worldZ + NormalStep);
        float yz = nz > 0 ? HeightFromNear(grid, nz, worldX, worldZ + NormalStep) : worldY;
        return new Vector3(worldY - yx, NormalStep, worldY - yz).Normalized();
    }

    /// <summary>
    /// Finds the tiles that shape the ground at a point and their weights. Fills the
    /// scratch arrays and returns how many entries are valid.
    /// </summary>
    private static int Gather(HexGridManager grid, float worldX, float worldZ)
    {
        // The grid's position is read once per grid, not once per sample: it is an
        // engine call, and a surround build makes a few hundred thousand samples.
        if (!ReferenceEquals(grid, _originGrid))
        {
            _originGrid = grid;
            _origin = grid.GlobalPosition;
        }
        Vector3 origin = _origin;
        float lx = worldX - origin.X;
        float lz = worldZ - origin.Z;

        float radius = grid.HexRadius;
        float spacing = radius * Sqrt3;
        float reach = Mathf.Clamp(grid.GroundBlendRadius, 0.6f, 0.86f);
        float core = Mathf.Clamp(grid.GroundCoreRadius, 0f, reach - 0.1f);

        Vector2I nearest = NearestAxial(radius, lx, lz);

        int n = 0;
        for (int s = 0; s < MaxNear; s++)
        {
            Vector2I axial = s == 0 ? nearest : nearest + HexDirection.All[s - 1];
            TileData tile = grid.GetTileOrVista(axial);
            if (tile == null)
                continue;

            Vector3 centre = grid.AxialToWorld(axial);
            float dx = lx - centre.X;
            float dz = lz - centre.Z;
            float d = Mathf.Sqrt(dx * dx + dz * dz) / spacing;
            float k = 1f - SmoothStep(core, reach, d);
            if (k <= 0f)
                continue;

            _nearTile[n] = tile;
            _nearReach[n] = k;
            n++;
        }

        if (n == 0)
            return 0;

        // How much the tiles in reach disagree about the height, in height steps.
        // Smooth in the position, so the sharpening below never makes a jump.
        float sumK = 0f;
        float mean = 0f;
        for (int i = 0; i < n; i++)
        {
            sumK += _nearReach[i];
            mean += _nearReach[i] * _nearTile[i].Height;
        }
        mean /= sumK;

        float spread = 0f;
        for (int i = 0; i < n; i++)
            spread += _nearReach[i] * Mathf.Abs(_nearTile[i].Height - mean);
        spread /= sumK;

        // Two tiles exactly at the cliff threshold have a spread of threshold / 2 at
        // their shared edge. Above that, sharpen: the drop becomes a steep face.
        float cliffAt = grid.CliffHeightThreshold * 0.5f;
        float sharpen = 1f + Mathf.Max(grid.GroundCliffSharpness, 0f) * SmoothStep(cliffAt, cliffAt + 0.5f, spread);

        for (int i = 0; i < n; i++)
            _nearWeight[i] = Mathf.Pow(_nearReach[i], sharpen);

        return n;
    }

    /// <summary>Weighted mean height of the gathered tiles, plus each terrain's blended noise.</summary>
    private static float HeightFromNear(HexGridManager grid, int n, float worldX, float worldZ)
    {
        float sumW = 0f;
        float sumH = 0f;
        for (int i = 0; i < n; i++)
        {
            sumW += _nearWeight[i];
            sumH += _nearWeight[i] * _nearTile[i].Height * HexTile.HeightStep;
        }
        if (sumW <= 0f)
            return 0f;

        // One noise sample per distinct terrain, weighted by that terrain's share.
        float noise = 0f;
        for (int i = 0; i < n; i++)
        {
            TileTerrainType terrain = _nearTile[i].TerrainType;
            bool seen = false;
            for (int j = 0; j < i; j++)
            {
                if (_nearTile[j].TerrainType == terrain)
                {
                    seen = true;
                    break;
                }
            }
            if (seen)
                continue;

            float share = 0f;
            for (int j = i; j < n; j++)
            {
                if (_nearTile[j].TerrainType == terrain)
                    share += _nearWeight[j];
            }

            (float amp, float freq) = HexMeshBuilder.NoiseParams(grid, terrain);
            if (amp > 0f)
                noise += share * amp * HexMeshBuilder.SurfaceNoise(worldX, worldZ, freq);
        }

        return (sumH + noise) / sumW;
    }

    // ────────────────────────────────────────────────────────
    // Texture weights
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Terrain shares for the texture at the gathered point, written to
    /// terrains/weights starting at offset. Returns how many layers were written
    /// (at most MaxLayers). The shares are sharper than the height weights, so a
    /// tile keeps its own ground out to near its edge and the handoff is organic
    /// but local.
    /// Water rule (same as the old builder): land never takes the water texture,
    /// and on a water tile the land texture runs fully to the shared edge, then
    /// fades toward the middle of the bed. Both sides agree at the edge.
    /// </summary>
    private static int TextureShares(HexGridManager grid, int n, TileData owner,
        TileTerrainType[] terrains, float[] weights, int offset)
    {
        float sharp = Mathf.Max(grid.GroundTextureSharpness, 1f);

        int count = 0;
        for (int i = 0; i < n; i++)
        {
            float w = Mathf.Pow(_nearWeight[i], sharp);
            if (w <= 0f)
                continue;

            TileTerrainType terrain = _nearTile[i].TerrainType;
            int slot = -1;
            for (int s = 0; s < count; s++)
            {
                if (terrains[offset + s] == terrain)
                {
                    slot = s;
                    break;
                }
            }

            if (slot >= 0)
            {
                weights[offset + slot] += w;
            }
            else if (count < MaxLayers)
            {
                terrains[offset + count] = terrain;
                weights[offset + count] = w;
                count++;
            }
        }

        if (count == 0)
        {
            terrains[offset] = owner.TerrainType;
            weights[offset] = 1f;
            return 1;
        }

        float land = 0f;
        float water = 0f;
        for (int s = 0; s < count; s++)
        {
            if (terrains[offset + s] == TileTerrainType.Water)
                water += weights[offset + s];
            else
                land += weights[offset + s];
        }

        if (water > 0f && land > 0f)
        {
            float total = land + water;
            // Land tile: no water texture at all. Water tile: land is boosted so it
            // reaches 100% where it holds a third or more (an edge or a corner).
            float landShare = owner.TerrainType == TileTerrainType.Water
                ? Mathf.Min(1f, 3f * land / total)
                : 1f;
            float landScale = landShare / land;
            float waterScale = (1f - landShare) / water;
            for (int s = 0; s < count; s++)
            {
                weights[offset + s] *= terrains[offset + s] == TileTerrainType.Water ? waterScale : landScale;
            }
        }
        else
        {
            float total = land + water;
            for (int s = 0; s < count; s++)
                weights[offset + s] /= total;
        }

        return count;
    }

    // ────────────────────────────────────────────────────────
    // Tile mesh
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// The patch of the ground surface over one tile's hex, in the tile's local
    /// space (Y relative to the tile top, like the old builder). Six triangular
    /// sectors, each a subdivided lattice. Points on the hex edge are computed from
    /// the world position only, so the neighbour's patch meets this one exactly.
    /// Edges with no neighbour (the rim of the world) get a skirt down to the floor.
    /// </summary>
    public static ArrayMesh BuildTileMesh(HexGridManager grid, TileData tile, float worldFloor, bool splatMode)
    {
        if (tile.TileView == null)
            return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (splatMode)
            st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);

        AppendTile(st, grid, tile, worldFloor, splatMode, worldSpace: false);

        st.GenerateTangents();
        return st.Commit();
    }

    /// <summary>
    /// Rows of triangles from this tile's centre to its edge. Playable tiles and the
    /// out-of-bounds rings nearest the board use grid.GroundSubdiv. Rings beyond
    /// grid.SurroundFineRings use half of it, when it is even and at least 4, so every
    /// coarse edge point is also a fine edge point.
    /// </summary>
    public static int SubdivFor(HexGridManager grid, TileData tile)
    {
        int fine = Math.Clamp(grid.GroundSubdiv, 2, 12);
        if (tile.VistaRing < grid.SurroundFineRings || fine < 4 || fine % 2 != 0)
            return fine;
        return fine / 2;
    }

    /// <summary>
    /// Adds one tile's patch of the ground to a surface being built. worldSpace false:
    /// vertices are in the tile's local space (its own mesh instance). worldSpace true:
    /// vertices are in world space, for a mesh merged from many tiles.
    /// Where this tile is coarse and the neighbour across an edge is fine, the outer row
    /// of triangles is split at the fine tile's extra edge points, so the two patches
    /// share every point along the edge and no crack can open between them.
    /// </summary>
    public static void AppendTile(SurfaceTool st, HexGridManager grid, TileData tile, float worldFloor,
        bool splatMode, bool worldSpace)
    {
        if (tile.TileView == null)
            return;

        float radius = grid.HexRadius;
        float ownTop = tile.Height * HexTile.HeightStep;
        Vector3 tilePos = tile.TileView.GlobalPosition;
        Vector2 centerXZ = new Vector2(tilePos.X, tilePos.Z);
        // Added to every emitted position: to world, or down to the tile-local frame.
        Vector3 shift = worldSpace ? new Vector3(centerXZ.X, 0f, centerXZ.Y) : new Vector3(0f, -ownTop, 0f);
        Vector2 uvCenter = worldSpace ? Vector2.Zero : centerXZ;
        float floorY = worldSpace ? worldFloor : worldFloor - ownTop;
        int div = SubdivFor(grid, tile);

        int lattice = (div + 1) * (div + 2) / 2;
        int points = lattice + div;   // plus one stitch point per segment of a sector's outer edge
        var pos = new Vector3[points];
        var nrm = new Vector3[points];
        var layerCount = new int[points];
        var layerTerrain = new TileTerrainType[points * MaxLayers];
        var layerWeight = new float[points * MaxLayers];
        var vista = new Vector2[points];

        void FillPoint(int p, Vector2 xz)
        {
            float wx = centerXZ.X + xz.X;
            float wz = centerXZ.Y + xz.Y;

            int near = Gather(grid, wx, wz);
            float y = near > 0 ? HeightFromNear(grid, near, wx, wz) : ownTop;
            if (near > 0)
            {
                layerCount[p] = TextureShares(grid, near, tile, layerTerrain, layerWeight, p * MaxLayers);
                vista[p] = VistaFromNear(near);
            }
            else
            {
                layerCount[p] = 1;
                layerTerrain[p * MaxLayers] = tile.TerrainType;
                layerWeight[p * MaxLayers] = 1f;
                vista[p] = VistaFlag + (tile.VistaHorizon >= 0f ? new Vector2(1f, tile.VistaHorizon) : Vector2.Zero);
            }

            // Normal() gathers again and overwrites the scratch arrays, so it
            // runs after the height and the texture shares are taken.
            nrm[p] = Normal(grid, wx, wz, y);
            pos[p] = new Vector3(xz.X + shift.X, y + shift.Y, xz.Y + shift.Z);
        }

        for (int e = 0; e < 6; e++)
        {
            Vector2 cA = Corner(e, radius);
            Vector2 cB = Corner((e + 1) % 6, radius);

            for (int i = 0; i <= div; i++)
            {
                for (int j = 0; j <= div - i; j++)
                    FillPoint(PointIndex(i, j, div), (cA * i + cB * j) / div);
            }

            // A finer neighbour has one extra point in the middle of each of this
            // edge's segments. Take those points too.
            TileData beyond = grid.GetTileOrVista(tile.Axial + HexDirection.All[(6 - e) % 6]);
            bool stitch = beyond != null && SubdivFor(grid, beyond) == div * 2;
            if (stitch)
            {
                for (int s = 0; s < div; s++)
                    FillPoint(lattice + s, (cA * (2 * (div - s) - 1) + cB * (2 * s + 1)) / (2 * div));
            }

            // Same winding as the old fan: (centre side, toward cA, toward cB).
            for (int i = 0; i < div; i++)
            {
                for (int j = 0; j < div - i; j++)
                {
                    int a = PointIndex(i, j, div);
                    int b = PointIndex(i + 1, j, div);
                    int c = PointIndex(i, j + 1, div);

                    if (stitch && i + j == div - 1)
                    {
                        // b and c are on the outer edge, segment j. Split at its midpoint.
                        int m = lattice + j;
                        EmitTriangle(st, grid, uvCenter, splatMode, pos, nrm, layerCount, layerTerrain, layerWeight, vista, a, b, m);
                        EmitTriangle(st, grid, uvCenter, splatMode, pos, nrm, layerCount, layerTerrain, layerWeight, vista, a, m, c);
                    }
                    else
                    {
                        EmitTriangle(st, grid, uvCenter, splatMode, pos, nrm, layerCount, layerTerrain, layerWeight, vista, a, b, c);
                    }

                    if (i + j < div - 1)
                    {
                        EmitTriangle(st, grid, uvCenter, splatMode, pos, nrm, layerCount, layerTerrain, layerWeight, vista,
                            b, PointIndex(i + 1, j + 1, div), c);
                    }
                }
            }

            // Rim of the world: no tile beyond this edge, so close the side.
            if (beyond == null)
            {
                Vector2 outward2 = (cA + cB).Normalized();
                Vector3 outward = new Vector3(outward2.X, 0f, outward2.Y);
                for (int s = 0; s < div; s++)
                {
                    EmitSkirtQuad(st, grid, uvCenter, splatMode, pos, layerCount, layerTerrain, layerWeight, vista,
                        PointIndex(div - s, s, div), PointIndex(div - s - 1, s + 1, div), floorY, outward);
                }
            }
        }
    }

    /// <summary>Marks UV2 as carrying the out-of-bounds fade. The shader reads X above 1.5 as
    /// "this mesh supplies the fade per vertex" and falls back to its per-tile value otherwise.</summary>
    private static readonly Vector2 VistaFlag = new Vector2(2f, 0f);

    /// <summary>
    /// Out-of-bounds dimming at the gathered point, for UV2: X = 2 + how muted (0 on the
    /// board, 1 out of bounds), Y = how far toward the horizon colour. Blended across
    /// tiles like the height, so there is no step from one ring of tiles to the next.
    /// The mute follows the horizon value, so it comes in with distance from the board
    /// and a playable tile is never dimmed.
    /// </summary>
    private static Vector2 VistaFromNear(int n)
    {
        float sumW = 0f;
        float outside = 0f;
        float horizon = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = _nearWeight[i];
            sumW += w;
            if (_nearTile[i].VistaHorizon >= 0f)
            {
                outside += w;
                horizon += w * _nearTile[i].VistaHorizon;
            }
        }
        if (sumW <= 0f)
            return VistaFlag;

        // The mute comes in with distance, not at the board's edge: ground just outside
        // the board should look like the ground just inside it. A playable tile has
        // horizon 0, so it is never dimmed.
        float blended = horizon / sumW;
        float mute = SmoothStep(0.2f, 0.75f, blended);
        return new Vector2(2f + mute, blended);
    }

    /// <summary>Index of lattice point (i toward cA, j toward cB) in a sector's row-major arrays.</summary>
    private static int PointIndex(int i, int j, int div)
        => i * (div + 1) - i * (i - 1) / 2 + j;

    private static void EmitTriangle(SurfaceTool st, HexGridManager grid, Vector2 centerXZ, bool splatMode,
        Vector3[] pos, Vector3[] nrm, int[] layerCount, TileTerrainType[] layerTerrain, float[] layerWeight,
        Vector2[] vista, int a, int b, int c)
    {
        if (!splatMode)
        {
            AddVertex(st, pos[a], nrm[a], BlendedColor(grid, layerCount, layerTerrain, layerWeight, a), centerXZ, false, default, vista[a]);
            AddVertex(st, pos[b], nrm[b], BlendedColor(grid, layerCount, layerTerrain, layerWeight, b), centerXZ, false, default, vista[b]);
            AddVertex(st, pos[c], nrm[c], BlendedColor(grid, layerCount, layerTerrain, layerWeight, c), centerXZ, false, default, vista[c]);
            return;
        }

        PickSlots(layerCount, layerTerrain, layerWeight, a, b, c, out TileTerrainType t0, out TileTerrainType t1, out TileTerrainType t2);
        var indices = new Color((int)t0, (int)t1, (int)t2, (int)t0);

        AddVertex(st, pos[a], nrm[a], SlotWeights(layerCount, layerTerrain, layerWeight, a, t0, t1, t2), centerXZ, true, indices, vista[a]);
        AddVertex(st, pos[b], nrm[b], SlotWeights(layerCount, layerTerrain, layerWeight, b, t0, t1, t2), centerXZ, true, indices, vista[b]);
        AddVertex(st, pos[c], nrm[c], SlotWeights(layerCount, layerTerrain, layerWeight, c, t0, t1, t2), centerXZ, true, indices, vista[c]);
    }

    private static void EmitSkirtQuad(SurfaceTool st, HexGridManager grid, Vector2 centerXZ, bool splatMode,
        Vector3[] pos, int[] layerCount, TileTerrainType[] layerTerrain, float[] layerWeight,
        Vector2[] vista, int a, int b, float floorLocal, Vector3 outward)
    {
        Vector3 topA = pos[a];
        Vector3 topB = pos[b];
        var botA = new Vector3(topA.X, floorLocal, topA.Z);
        var botB = new Vector3(topB.X, floorLocal, topB.Z);

        Color colTopA, colTopB, colBotA, colBotB;
        Color indices = default;
        if (splatMode)
        {
            PickSlots(layerCount, layerTerrain, layerWeight, a, b, a, out TileTerrainType t0, out TileTerrainType t1, out TileTerrainType t2);
            indices = new Color((int)t0, (int)t1, (int)t2, (int)t0);
            colTopA = SlotWeights(layerCount, layerTerrain, layerWeight, a, t0, t1, t2);
            colTopB = SlotWeights(layerCount, layerTerrain, layerWeight, b, t0, t1, t2);
            // Alpha 1 is the skirt flag the shader reads (tops leave it 0).
            colTopA.A = 1f;
            colTopB.A = 1f;
            colBotA = colTopA;
            colBotB = colTopB;
        }
        else
        {
            colTopA = BlendedColor(grid, layerCount, layerTerrain, layerWeight, a);
            colTopB = BlendedColor(grid, layerCount, layerTerrain, layerWeight, b);
            colBotA = colTopA * SkirtFloorDarkening;
            colBotA.A = 1f;
            colBotB = colTopB * SkirtFloorDarkening;
            colBotB.A = 1f;
        }

        // Same vertex order as the old wall quad, so the face points outward.
        AddVertex(st, topA, outward, colTopA, centerXZ, splatMode, indices, vista[a]);
        AddVertex(st, botA, outward, colBotA, centerXZ, splatMode, indices, vista[a]);
        AddVertex(st, botB, outward, colBotB, centerXZ, splatMode, indices, vista[b]);

        AddVertex(st, topA, outward, colTopA, centerXZ, splatMode, indices, vista[a]);
        AddVertex(st, botB, outward, colBotB, centerXZ, splatMode, indices, vista[b]);
        AddVertex(st, topB, outward, colTopB, centerXZ, splatMode, indices, vista[b]);
    }

    /// <summary>
    /// The three terrains a triangle draws with: the largest summed shares across
    /// its three points. The layer indices must be constant over a triangle (the
    /// shader cannot interpolate them), only the weights vary.
    /// </summary>
    private static void PickSlots(int[] layerCount, TileTerrainType[] layerTerrain, float[] layerWeight,
        int a, int b, int c, out TileTerrainType t0, out TileTerrainType t1, out TileTerrainType t2)
    {
        Span<int> terrains = stackalloc int[MaxLayers * 3];
        Span<float> sums = stackalloc float[MaxLayers * 3];
        int count = 0;

        Span<int> pts = stackalloc int[3];
        pts[0] = a;
        pts[1] = b;
        pts[2] = c;

        for (int v = 0; v < 3; v++)
        {
            int p = pts[v];
            for (int s = 0; s < layerCount[p]; s++)
            {
                int terrain = (int)layerTerrain[p * MaxLayers + s];
                float w = layerWeight[p * MaxLayers + s];
                int found = -1;
                for (int k = 0; k < count; k++)
                {
                    if (terrains[k] == terrain)
                    {
                        found = k;
                        break;
                    }
                }
                if (found >= 0)
                {
                    sums[found] += w;
                }
                else
                {
                    terrains[count] = terrain;
                    sums[count] = w;
                    count++;
                }
            }
        }

        Span<int> picked = stackalloc int[3];
        for (int slot = 0; slot < 3; slot++)
        {
            int best = -1;
            for (int k = 0; k < count; k++)
            {
                if (sums[k] >= 0f && (best < 0 || sums[k] > sums[best]))
                    best = k;
            }

            if (best < 0)
            {
                picked[slot] = picked[0];
            }
            else
            {
                picked[slot] = terrains[best];
                sums[best] = -1f;   // taken
            }
        }

        t0 = (TileTerrainType)picked[0];
        t1 = (TileTerrainType)picked[1];
        t2 = (TileTerrainType)picked[2];
    }

    /// <summary>A point's shares of the triangle's three chosen terrains, as splat weights. Alpha stays 0 (not a skirt).</summary>
    private static Color SlotWeights(int[] layerCount, TileTerrainType[] layerTerrain, float[] layerWeight,
        int p, TileTerrainType t0, TileTerrainType t1, TileTerrainType t2)
    {
        float w0 = 0f, w1 = 0f, w2 = 0f;
        for (int s = 0; s < layerCount[p]; s++)
        {
            TileTerrainType terrain = layerTerrain[p * MaxLayers + s];
            float w = layerWeight[p * MaxLayers + s];
            if (terrain == t0)
                w0 += w;
            else if (terrain == t1)
                w1 += w;
            else if (terrain == t2)
                w2 += w;
        }

        float total = w0 + w1 + w2;
        if (total <= 0.0001f)
            return new Color(1f, 0f, 0f, 0f);
        return new Color(w0 / total, w1 / total, w2 / total, 0f);
    }

    /// <summary>Vertex-colour fallback (no splat shader): the shares mixed into one colour.</summary>
    private static Color BlendedColor(HexGridManager grid, int[] layerCount, TileTerrainType[] layerTerrain, float[] layerWeight, int p)
    {
        Color sum = new Color(0f, 0f, 0f, 0f);
        float total = 0f;
        for (int s = 0; s < layerCount[p]; s++)
        {
            float w = layerWeight[p * MaxLayers + s];
            sum += HexMeshBuilder.TerrainColor(grid, layerTerrain[p * MaxLayers + s]) * w;
            total += w;
        }
        if (total <= 0.0001f)
            return Colors.White;

        Color c = sum / total;
        c.A = 1f;
        return c;
    }

    private static void AddVertex(SurfaceTool st, Vector3 local, Vector3 normal, Color color,
        Vector2 centerXZ, bool splat, Color custom, Vector2 vista)
    {
        st.SetNormal(normal);
        st.SetUV2(vista);
        st.SetColor(color);
        if (splat)
            st.SetCustom(0, custom);
        st.SetUV(new Vector2((centerXZ.X + local.X) * 0.25f, (centerXZ.Y + local.Z) * 0.25f));
        st.AddVertex(local);
    }

    // ────────────────────────────────────────────────────────
    // Hex helpers
    // ────────────────────────────────────────────────────────

    /// <summary>Flat-top corner i at 60 degrees times i, the same ring as HexMeshBuilder.</summary>
    private static Vector2 Corner(int i, float radius)
    {
        float a = Mathf.DegToRad(60f * i);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
    }

    /// <summary>Axial coordinate of the hex whose centre is nearest a grid-local XZ (cube rounding).</summary>
    private static Vector2I NearestAxial(float radius, float localX, float localZ)
    {
        float q = localX / (1.5f * radius);
        float r = localZ / (Sqrt3 * radius) - q * 0.5f;
        float y = -q - r;

        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int ry = Mathf.RoundToInt(y);

        float dq = Mathf.Abs(rq - q);
        float dr = Mathf.Abs(rr - r);
        float dy = Mathf.Abs(ry - y);

        if (dq > dr && dq > dy)
            rq = -rr - ry;
        else if (dr > dy)
            rr = -rq - ry;

        return new Vector2I(rq, rr);
    }

    private static float SmoothStep(float from, float to, float x)
    {
        if (to <= from)
            return x < from ? 0f : 1f;
        float t = Mathf.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
