using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.PainterlyGrass.cs  (partial of HexGridManager)
//
// Animated ground-cover grass over grass/forest tiles. Scatters
// across the FULL hexagon (so adjacent grass tiles tessellate into a
// continuous carpet reaching every edge), with grass-aware overlap:
// blades may bleed slightly past an edge ONLY toward a grass neighbour,
// hiding the seam; edges facing non-grass / the map boundary clip hard.
//
// MESH PALETTE: assign one or more weighted GrassMeshVariant resources in
// GrassMeshVariants to mix blade shapes. Each variant becomes its own
// MultiMeshInstance3D (its own draw call) but they all share the single
// grass ShaderMaterial, so wind/tint stay perfectly in phase across them.
// Per variant you also get a scale band, a flat tint, and grass/forest
// terrain eligibility. Variant selection + scale use a SEPARATE RNG
// stream, so adding variants does NOT change blade positions for an
// existing MapSeed (and a default 1->1 scale band draws no random number
// at all, keeping legacy fields byte-identical).
//
// All [Export] fields carry /// XML doc summaries so they show as
// tooltips in the Godot inspector. (Requires the project to generate
// the XML documentation file — GenerateDocumentationFile in the .csproj.
// If your already-commented exports show tooltips, this is already on.)
//
// INTEGRATION: one line at the tail of GenerateMap(), after
//   SpawnObstacleVisuals();
//       SpawnPainterlyGrass();
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Painterly Grass")]

    /** Master toggle. When off, no grass is spawned and any existing field is cleared. */
    [Export] public bool EnablePainterlyGrass = true;

    /** Explicit grass shader. If left null, the shader is loaded from PainterlyGrassShaderPath. */
    [Export] public Shader PainterlyGrassShader;

    /** Fallback load path for the grass shader, used only when PainterlyGrassShader is null. */
    [Export] public string PainterlyGrassShaderPath = "res://Assets/Shaders/painterly_grass.gdshader";

    /** Explicit grass material. If set, it is used AS-IS — the automatic wind_noise injection is skipped, so you must set the shader's wind_noise slot yourself on this material. Leave null to let the builder create one and wire wind_noise for you. */
    [Export] public Material PainterlyGrassMaterial;

    /** Weighted mesh palette. Add GrassMeshVariant entries to mix blade shapes; each blade picks one at random by Weight (filtered by terrain eligibility). Empty = fall back to PainterlyGrassMesh (or the procedural blade). Each variant is a separate draw call, so keep this to a few. */
    [Export] public GrassMeshVariant[] GrassMeshVariants;

    /** Single fallback blade mesh, used only when GrassMeshVariants is empty. If this is also null, a procedural two-quad blade is built at GrassBladeHeight. */
    [Export] public Mesh PainterlyGrassMesh;

    /** Print per-variant blade counts to the output log after each spawn — handy for confirming your Weight ratios produced the blade distribution you expected. */
    [Export] public bool GrassDebugLog = false;

    /** Overall blade scale multiplier (applied to both width and height, before per-variant scale and per-blade jitter). */
    [Export(PropertyHint.Range, "0.05,3.0,0.05")] public float GrassScale = 1.0f;

    /** Base blade count per tile. Forest tiles get ×1.35 and the map density preset scales it further (Sparse 0.5 → Wild 1.8). */
    [Export(PropertyHint.Range, "0,800,1")] public int GrassBladesPerTile = 400;

    /** Height of the PROCEDURAL blade mesh. Ignored when a GrassMeshVariants entry or PainterlyGrassMesh is assigned. */
    [Export(PropertyHint.Range, "0.1,1.5,0.05")] public float GrassBladeHeight = 0.3f;

    /** How far blades may bleed past an edge toward a grass neighbour (fraction of HexRadius). Hides grass-grass seams; 0 = clip exactly to the hex. */
    [Export(PropertyHint.Range, "0.0,0.4,0.01")] public float GrassEdgeOverlap = 0.05f;

    /** Per-blade random height variation (± fraction). Higher = more varied blade heights. */
    [Export(PropertyHint.Range, "0,0.6,0.05")] public float GrassHeightJitter = 0.6f;

    /** Per-blade random width variation (± fraction). Higher = more varied blade widths. */
    [Export(PropertyHint.Range, "0,0.6,0.05")] public float GrassWidthJitter = 0.6f;

    /** Also scatter grass on Forest tiles (with a denser blade count). Off = grass on Grass tiles only. */
    [Export] public bool GrassOnForest = false;

    /** 0 = even meadow; 1 = grass fully driven by the clump field (dense clumps + thin gaps). */
    [Export(PropertyHint.Range, "0,1,0.05")] public float GrassClumpInfluence = 0.35f;

    /** World-space frequency of the clump field. Lower = bigger clumps and bigger bare patches. Flowers/rocks share this field, so changing it shifts where they cluster too. */
    [Export] public float GrassClumpFrequency = 0.35f;

    /** Clump-noise value below which ground is left fully bare (hard pockets). 0 = no hard bare spots. */
    [Export(PropertyHint.Range, "0,1,0.02")] public float GrassBareThreshold = 0.3f;

    /** Tile span of a grass chunk: N×N axial tiles share one MultiMesh per variant. Chunking (vs one map-wide MultiMesh) gives the engine real frustum-culling granularity and gives the LOD controller something to thin per distance. Smaller = tighter culling + finer LOD but more draw calls. */
    [Export(PropertyHint.Range, "1,8,1")] public int GrassChunkTiles = 3;

    /** Distance-density LOD: far chunks draw a uniform random subsample of their blades (via VisibleInstanceCount), saving the vertex work the dither fade alone cannot (discarded fragments still cost their vertices). */
    [Export] public bool GrassLodEnabled = true;

    /** Camera distance up to which a chunk draws 100% of its blades. Keep at or below the grass material's fade_start so LOD thinning hides inside the dither fade. */
    [Export(PropertyHint.Range, "4,80,1")] public float GrassLodFullDistance = 24f;

    /** Camera distance at which a chunk draws ZERO blades. MUST be >= the grass material's fade_end — the dither fade has fully dissolved blades by then, so culling them is visually free. */
    [Export(PropertyHint.Range, "8,120,1")] public float GrassLodEndDistance = 55f;

    /** Blade-fraction floor a chunk tapers to as it approaches GrassLodEndDistance (before the zero cut). */
    [Export(PropertyHint.Range, "0.05,1.0,0.05")] public float GrassLodMinFraction = 0.35f;

    private const string PainterlyGrassGroup = "painterly_grass";
    private Material _painterlyGrassMaterialCache;

    public void SpawnPainterlyGrass()
    {
        ClearPainterlyGrass();

        if (!EnablePainterlyGrass)
            return;

        Material mat = ResolvePainterlyGrassMaterial();
        if (mat == null)
            return;

        // ---- Resolve the effective weighted mesh palette ----
        // Explicit weighted variants if any are valid, else the single
        // PainterlyGrassMesh, else a procedural blade.
        var meshes = new List<Mesh>();
        var weights = new List<float>();
        var sMin = new List<float>();
        var sMax = new List<float>();
        var tints = new List<Color>();
        var onGrass = new List<bool>();
        var onForest = new List<bool>();
        var clumpInfl = new List<float>();
        var clumpScale = new List<float>();
        var clumpCov = new List<float>();

        if (GrassMeshVariants != null)
        {
            foreach (var v in GrassMeshVariants)
            {
                if (v == null || v.Mesh == null)
                    continue;
                meshes.Add(v.Mesh);
                weights.Add(Mathf.Max(0f, v.Weight));
                float lo = Mathf.Max(0.01f, Mathf.Min(v.ScaleMin, v.ScaleMax));
                float hi = Mathf.Max(0.01f, Mathf.Max(v.ScaleMin, v.ScaleMax));
                sMin.Add(lo);
                sMax.Add(hi);
                tints.Add(v.Tint);
                onGrass.Add(v.AllowOnGrass);
                onForest.Add(v.AllowOnForest);
                clumpInfl.Add(Mathf.Clamp(v.ClumpInfluence, 0f, 1f));
                clumpScale.Add(Mathf.Max(0.001f, v.ClumpScale));
                clumpCov.Add(Mathf.Clamp(v.ClumpCoverage, 0f, 1f));
            }
        }

        if (meshes.Count == 0)
        {
            meshes.Add(PainterlyGrassMesh ?? BuildProceduralBladeMesh(GrassBladeHeight));
            weights.Add(1f);
            sMin.Add(1f);
            sMax.Add(1f);
            tints.Add(Colors.White);
            onGrass.Add(true);
            onForest.Add(true);
            clumpInfl.Add(0f);
            clumpScale.Add(0.08f);
            clumpCov.Add(0.4f);
        }

        int variantCount = meshes.Count;

        // Per-CHUNK, per-variant transform buckets (2026-07-15). A chunk is an
        // N×N-tile block; each non-empty (chunk, variant) pair becomes its own
        // MultiMeshInstance3D with a tight CustomAabb, so offscreen chunks
        // frustum-cull for real and the LOD controller can thin far chunks.
        // NOTE: bucketing by tile does NOT change any RNG draw order — blade
        // positions stay byte-identical for a given MapSeed.
        int chunkSpan = Mathf.Max(1, GrassChunkTiles);
        var chunkBuckets = new Dictionary<Vector2I, List<Transform3D>[]>();

        List<Transform3D>[] BucketsFor(Vector2I axial)
        {
            var key = new Vector2I(
                Mathf.FloorToInt(axial.X / (float)chunkSpan),
                Mathf.FloorToInt(axial.Y / (float)chunkSpan));
            if (!chunkBuckets.TryGetValue(key, out var arr))
            {
                arr = new List<Transform3D>[variantCount];
                for (int v = 0; v < variantCount; v++)
                    arr[v] = new List<Transform3D>();
                chunkBuckets[key] = arr;
            }
            return arr;
        }

        // Eligible variant indices per terrain. Weights are now POSITION-
        // dependent (clump gating), so the cumulative table can't be precomputed
        // here — only the eligible set is. Falls back to ALL variants if a
        // terrain's eligible subset is empty, so grass never silently vanishes.
        int[] BuildPickerIds(System.Func<int, bool> eligible)
        {
            var ids = new List<int>();
            for (int v = 0; v < variantCount; v++)
                if (eligible(v))
                    ids.Add(v);
            if (ids.Count == 0)
                for (int v = 0; v < variantCount; v++)
                    ids.Add(v);
            return ids.ToArray();
        }

        var grassIds = BuildPickerIds(v => onGrass[v]);
        var forestIds = BuildPickerIds(v => onForest[v]);

        // Per-variant clump field (one independently-seeded noise per variant
        // that uses it) so a variant's pockets are connected and don't coincide
        // with another variant's. Null for variants with ClumpInfluence == 0.
        var clumpNoiseV = new FastNoiseLite[variantCount];
        for (int v = 0; v < variantCount; v++)
        {
            if (clumpInfl[v] <= 0f)
                continue;
            clumpNoiseV[v] = new FastNoiseLite
            {
                Seed = unchecked(MapSeed ^ (0x436C0000 + v * 0x00010001)),
                Frequency = clumpScale[v],
                NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth
            };
        }

        float densityScalar = DensityPreset switch
        {
            MapDensityPreset.Sparse => 0.5f,
            MapDensityPreset.Standard => 1.0f,
            MapDensityPreset.Dense => 1.4f,
            MapDensityPreset.Wild => 1.8f,
            _ => 1.0f
        };

        var rng = new RandomNumberGenerator { Seed = (ulong)(MapSeed ^ 0x6B61736D) };

        // SEPARATE stream for variant selection + per-variant scale, so adding
        // or removing mesh variants does NOT perturb blade positions/scales for
        // a given seed. A degenerate (min==max) scale band draws nothing here,
        // keeping single-mesh legacy fields byte-identical.
        var variantRng = new RandomNumberGenerator { Seed = (ulong)(MapSeed ^ 0x4D455348) }; // "MESH"

        // Scratch buffer for live (position-dependent) effective weights,
        // reused every pick so we don't allocate per blade.
        var effW = new float[variantCount];

        // Position-aware weighted pick. Each eligible variant's weight is gated
        // by its clump field sampled at (wx,wz): inside the variant's pocket the
        // gate ~1 (full weight), outside it ~0 (absent), blended by ClumpInfluence.
        // A flat (ClumpInfluence == 0) variant keeps its constant weight and so
        // fills wherever the gated variants drop out. Uses a SINGLE variantRng
        // draw per call, identical to before when no variant clumps — so existing
        // seeds keep byte-identical variant assignment until clumping is enabled.
        int PickVariant(bool isForest, float wx, float wz)
        {
            var ids = isForest ? forestIds : grassIds;
            if (ids.Length == 1)
                return ids[0];

            float sum = 0f;
            for (int j = 0; j < ids.Length; j++)
            {
                int id = ids[j];
                float w = weights[id];
                var noise = clumpNoiseV[id];
                if (noise != null)
                {
                    float n = noise.GetNoise2D(wx, wz) * 0.5f + 0.5f;     // 0..1
                    float threshold = 1f - clumpCov[id];
                    const float soft = 0.15f;
                    float gate = Mathf.SmoothStep(threshold - soft, threshold + soft, n);
                    w *= Mathf.Lerp(1f, gate, clumpInfl[id]);
                }
                effW[j] = w;
                sum += w;
            }

            if (sum <= 0f) // every eligible variant gated out here -> uniform pick
                return ids[Mathf.Min(ids.Length - 1, (int)(variantRng.Randf() * ids.Length))];

            float r = variantRng.Randf() * sum;
            float acc = 0f;
            for (int j = 0; j < ids.Length; j++)
            {
                acc += effW[j];
                if (r < acc)
                    return ids[j];
            }
            return ids[ids.Length - 1];
        }

        float SampleVariantScale(int vIdx)
        {
            float lo = sMin[vIdx];
            float hi = sMax[vIdx];
            if (lo >= hi)
                return lo;                       // fixed band -> no RNG draw
            return variantRng.RandfRange(lo, hi);
        }

        var clumpNoise = new FastNoiseLite
        {
            Seed = unchecked(MapSeed ^ 0x13577531),
            Frequency = GrassClumpFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth
        };

        // Regular lattice: neighbour directions + apothem are constant everywhere.
        // AxialToWorld is linear with AxialToWorld(0,0) == origin, so the world
        // delta to neighbour k is simply AxialToWorld(HexDirs[k]).
        var nbrDir = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            Vector3 d = AxialToWorld(HexDirs[k]);
            nbrDir[k] = new Vector2(d.X, d.Z).Normalized();
        }
        Vector3 d0 = AxialToWorld(HexDirs[0]);
        float apothem = 0.5f * new Vector2(d0.X, d0.Z).Length();     // centre -> shared edge
        float reach = HexRadius * (1.0f + GrassEdgeOverlap);         // candidate disc covers corners + overlap
        float overlapDist = HexRadius * GrassEdgeOverlap;

        bool IsGrassy(TileData t) =>
            t.TerrainType == TileTerrainType.Grass ||
            (GrassOnForest && t.TerrainType == TileTerrainType.Forest);

        var nbrGrass = new bool[6];

        // Playable tiles at full density, vista tiles at VistaScatterDensity
        // (see HexGridManager.Vista.cs).
        foreach (var (tile, scatterDensity) in ScatterTiles())
        {
            if (tile.TileView == null || tile.IsBlocked || !IsGrassy(tile))
                continue;

            for (int k = 0; k < 6; k++)
            {
                var nc = tile.Axial + HexDirs[k];
                var nt = GetTileOrVista(nc); // vista counts — blades bleed into the surround
                nbrGrass[k] = nt != null && nt.TileView != null && !nt.IsBlocked && IsGrassy(nt);
            }

            bool isForest = tile.TerrainType == TileTerrainType.Forest;
            int baseCount = isForest
                ? Mathf.RoundToInt(GrassBladesPerTile * 1.35f)
                : GrassBladesPerTile;
            int count = Mathf.Max(0, Mathf.RoundToInt(baseCount * densityScalar * scatterDensity));

            // X/Z come from the tile centre; Y is sampled per-blade below so
            // the carpet hugs the blended mesh instead of forming a flat shelf.
            Vector3 top = tile.TileView.GlobalPosition;
            var tileBuckets = BucketsFor(tile.Axial);

            for (int i = 0; i < count; i++)
            {
                float ang = rng.RandfRange(0f, Mathf.Tau);
                float radc = reach * Mathf.Sqrt(rng.Randf()); // uniform over the disc
                var p = new Vector2(Mathf.Cos(ang) * radc, Mathf.Sin(ang) * radc);

                // Hexagon = intersection of 6 half-planes dot(p, nbrDir[k]) <= apothem.
                // Allow bleeding past edge k only toward a grass neighbour, up to overlapDist.
                bool ok = true;
                for (int k = 0; k < 6; k++)
                {
                    float dk = p.Dot(nbrDir[k]);
                    if (dk > apothem)
                    {
                        if (!nbrGrass[k] || dk > apothem + overlapDist)
                        {
                            ok = false;
                            break;
                        }
                    }
                }
                if (!ok)
                    continue;

                if (GrassClumpInfluence > 0f)
                {
                    float cnx = top.X + p.X;
                    float cnz = top.Z + p.Y;
                    float cn = clumpNoise.GetNoise2D(cnx, cnz) * 0.5f + 0.5f; // 0..1
                    if (cn < GrassBareThreshold)
                        continue;                                   // hard bare pocket
                    float accept = Mathf.Lerp(1f, cn, GrassClumpInfluence);
                    if (rng.Randf() > accept)
                        continue;                                   // thinned toward the gaps
                }

                float wx = top.X + p.X;
                float wz = top.Z + p.Y;
                var pos = new Vector3(wx, SampleGrassSurfaceY(tile, wx, wz), wz);

                int vIdx = PickVariant(isForest, wx, wz);
                float vScale = SampleVariantScale(vIdx);

                float yaw = rng.RandfRange(0f, Mathf.Tau);
                float hs = GrassScale * vScale * (1f + rng.RandfRange(-GrassHeightJitter, GrassHeightJitter));
                float ws = GrassScale * vScale * (1f + rng.RandfRange(-GrassWidthJitter, GrassWidthJitter));

                var basis = new Basis(Vector3.Up, yaw)
                    .Scaled(new Vector3(Mathf.Max(0.05f, ws), Mathf.Max(0.05f, hs), Mathf.Max(0.05f, ws)));

                tileBuckets[vIdx].Add(new Transform3D(basis, pos));
            }
        }

        int totalBlades = 0;
        var variantTotals = new int[variantCount];
        foreach (var lists in chunkBuckets.Values)
        {
            for (int v = 0; v < variantCount; v++)
            {
                variantTotals[v] += lists[v].Count;
                totalBlades += lists[v].Count;
            }
        }

        if (GrassDebugLog)
        {
            var sb = new System.Text.StringBuilder("[PainterlyGrass] blade counts: ");
            for (int v = 0; v < variantCount; v++)
                sb.Append($"#{v}={variantTotals[v]} ");
            sb.Append($"(total {totalBlades}, {chunkBuckets.Count} chunks)");
            GD.Print(sb.ToString());
        }

        if (totalBlades == 0)
            return;

        Node parent = PropParent ?? this;

        // Per-instance mesh height is ALWAYS written (cheap — 16 bytes/instance)
        // so stiffness_from_instance_height works regardless of variant count or
        // a null/empty palette slot. Gating this on variantCount once silently
        // disabled instance-height mode whenever a slot was empty -> never again.
        const bool writeHeights = true;

        // Optional distance-density LOD controller. Rebuilt with the field on
        // every spawn (it lives in the same group, so ClearPainterlyGrass frees
        // it); each chunk MultiMesh registers below and the controller thins
        // far chunks by lowering VisibleInstanceCount on a slow tick.
        GrassLodController lod = null;
        if (GrassLodEnabled)
        {
            lod = new GrassLodController
            {
                Name = "GrassLodController",
                FullDistance = GrassLodFullDistance,
                EndDistance = GrassLodEndDistance,
                MinFraction = GrassLodMinFraction
            };
            lod.AddToGroup(PainterlyGrassGroup);
            parent.AddChild(lod);
        }

        // One MultiMeshInstance3D per non-empty (chunk, variant) pair. All share
        // the single grass material so wind + mass tint stay in phase everywhere.
        foreach (var kv in chunkBuckets)
        {
            Vector2I chunkKey = kv.Key;
            var lists = kv.Value;

            for (int v = 0; v < variantCount; v++)
            {
                var list = lists[v];
                if (list.Count == 0)
                    continue;

                // Deterministic per-chunk shuffle of the instance buffer, so any
                // PREFIX of it is a spatially uniform subsample of the chunk —
                // that is what lets the LOD controller thin a chunk just by
                // lowering VisibleInstanceCount. Positions are untouched. Only
                // safe because the grass shader is OPAQUE (v7.2): draw order
                // within the buffer no longer affects the image.
                var shuffleRng = new RandomNumberGenerator
                {
                    Seed = (ulong)unchecked(MapSeed
                        ^ (chunkKey.X * 73856093)
                        ^ (chunkKey.Y * 19349663)
                        ^ (v * 83492791))
                };
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = shuffleRng.RandiRange(0, i);
                    (list[i], list[j]) = (list[j], list[i]);
                }

                bool tinted = tints[v] != Colors.White;

                // Object-space height of THIS variant's mesh. Written per-instance
                // (custom-data .r) so the shader can normalise the height gradient
                // PER MESH — a tall and a short mesh each get a correct 0->1 ramp.
                // Also drives the AABB padding below so tall meshes get a tall box.
                float meshHeight = meshes[v].GetAabb().Size.Y;
                if (meshHeight <= 0.0001f)
                    meshHeight = 1.0f;

                // On the Compatibility renderer, enabling custom data but NOT colors
                // leaves the shader's COLOR builtin reading an uninitialised instance
                // slot (black) -> ALBEDO = col * COLOR collapses to 0 and every blade
                // renders black regardless of base_color. So whenever custom data is
                // on we MUST also enable AND explicitly fill the colour slot
                // (white = inert tint hook). Pair them, always.
                bool useColors = tinted || writeHeights;
                Color instanceColor = tinted ? tints[v] : Colors.White;

                var mm = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = meshes[v],
                    UseColors = useColors,          // both flags must precede InstanceCount
                    UseCustomData = writeHeights
                };
                mm.InstanceCount = list.Count;

                var customHeight = new Color(meshHeight, 0f, 0f, 0f);
                for (int i = 0; i < list.Count; i++)
                {
                    mm.SetInstanceTransform(i, list[i]);
                    if (useColors)
                        mm.SetInstanceColor(i, instanceColor);
                    if (writeHeights)
                        mm.SetInstanceCustomData(i, customHeight);
                }

                // --- Explicit visibility AABB (per chunk) ---
                // Godot's auto-computed MultiMesh AABB is unreliable for world-space
                // scattered grass (whole field culls as one unit on a small camera
                // turn), so bounds are built from the actual instance origins. The
                // grow margin covers blade height × jitter plus wind sway — kept
                // TIGHT here (unlike the old map-wide box) so chunk frustum culling
                // and LOD distances stay accurate.
                Vector3 mn = list[0].Origin;
                Vector3 mx = mn;
                for (int i = 0; i < list.Count; i++)
                {
                    Vector3 o = list[i].Origin;
                    mn.X = Mathf.Min(mn.X, o.X);
                    mn.Y = Mathf.Min(mn.Y, o.Y);
                    mn.Z = Mathf.Min(mn.Z, o.Z);
                    mx.X = Mathf.Max(mx.X, o.X);
                    mx.Y = Mathf.Max(mx.Y, o.Y);
                    mx.Z = Mathf.Max(mx.Z, o.Z);
                }
                // Use the real mesh height (not GrassBladeHeight, which only sizes
                // the procedural blade) so imported tall meshes get a correct box.
                float worldBladeHeight = meshHeight * GrassScale * sMax[v];
                float grow = Mathf.Max(2.0f, worldBladeHeight * (1f + GrassHeightJitter) + 0.8f);
                mm.CustomAabb = new Aabb(mn, mx - mn).Grow(grow);

                var mmi = new MultiMeshInstance3D
                {
                    Name = $"PainterlyGrass_c{chunkKey.X}_{chunkKey.Y}_v{v}",
                    Multimesh = mm,
                    MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                mmi.AddToGroup(PainterlyGrassGroup);

                parent.AddChild(mmi);
                mmi.GlobalTransform = Transform3D.Identity; // instance transforms are world-space

                lod?.Register(mmi, mm.CustomAabb);
            }
        }
    }

    /** 
    /// Per-blade surface height, sampled the same way props are (analytic
    /// blended-mesh sample) so grass and props sit at identical heights on
    /// slopes. Legacy mode falls back to the flat tile top.
    ///  */
    private float SampleGrassSurfaceY(TileData tile, float worldX, float worldZ)
    {
        if (UseBlendedTerrainMesh && tile.TileView != null)
            return HexMeshBuilder.SampleSurfaceWorldY(
                this, tile, worldX, worldZ, TerrainSolidFactor, TerrainTerraceSteps);

        return tile.TileView != null ? tile.TileView.GlobalPosition.Y : 0f;
    }

    private void ClearPainterlyGrass()
    {
        Node parent = PropParent ?? this;
        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup(PainterlyGrassGroup))
                child.QueueFree();
        }
    }

    private Material ResolvePainterlyGrassMaterial()
    {
        if (PainterlyGrassMaterial != null)
            return PainterlyGrassMaterial;

        if (_painterlyGrassMaterialCache != null)
            return _painterlyGrassMaterialCache;

        Shader shader = PainterlyGrassShader;
        if (shader == null && !string.IsNullOrEmpty(PainterlyGrassShaderPath))
            shader = GD.Load<Shader>(PainterlyGrassShaderPath);

        if (shader == null)
        {
            GD.PushWarning($"[HexGridManager] Painterly grass shader not found " +
                $"(assign PainterlyGrassShader, or place it at '{PainterlyGrassShaderPath}'). Grass skipped.");
            return null;
        }

        var sm = new ShaderMaterial { Shader = shader };
        sm.SetShaderParameter("wind_noise", WindNoise.CreateSeamless());
        _painterlyGrassMaterialCache = sm;
        return sm;
    }

    private static Mesh BuildProceduralBladeMesh(float height)
    {
        const float baseHalf = 0.07f;
        const float tipHalf = 0.025f;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        void AddQuad(Vector3 right)
        {
            Vector3 bl = -right * baseHalf;
            Vector3 br = right * baseHalf;
            Vector3 tr = right * tipHalf + Vector3.Up * height;
            Vector3 tl = -right * tipHalf + Vector3.Up * height;

            st.SetUV(new Vector2(0f, 0f));
            st.AddVertex(bl);
            st.SetUV(new Vector2(1f, 0f));
            st.AddVertex(br);
            st.SetUV(new Vector2(1f, 1f));
            st.AddVertex(tr);

            st.SetUV(new Vector2(0f, 0f));
            st.AddVertex(bl);
            st.SetUV(new Vector2(1f, 1f));
            st.AddVertex(tr);
            st.SetUV(new Vector2(0f, 1f));
            st.AddVertex(tl);
        }

        AddQuad(new Vector3(1f, 0f, 0f));
        AddQuad(new Vector3(0f, 0f, 1f));

        st.GenerateNormals();
        st.GenerateTangents(); // required for the tangent-space normal map
        return st.Commit();
    }
}
