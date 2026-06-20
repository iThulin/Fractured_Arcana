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
// All [Export] fields carry /// XML doc summaries so they show as
// tooltips in the Godot inspector. (Requires the project to generate
// the XML documentation file — GenerateDocumentationFile in the .csproj.
// If your already-commented exports show tooltips, this is already on.)
//
// INTEGRATION: one line at the tail of GenerateMap(), after
//   SpawnTerrainPropsFromManifest();
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

    /** Blade mesh to instance. If null, a procedural two-quad blade is built at GrassBladeHeight. */
    [Export] public Mesh PainterlyGrassMesh;

    /** Overall blade scale multiplier (applied to both width and height, before per-blade jitter). */
    [Export(PropertyHint.Range, "0.05,3.0,0.05")] public float GrassScale = 1.0f;

    /** Base blade count per tile. Forest tiles get ×1.35 and the map density preset scales it further (Sparse 0.5 → Wild 1.8). */
    [Export(PropertyHint.Range, "0,800,1")] public int GrassBladesPerTile = 40;

    /** Height of the PROCEDURAL blade mesh. Ignored when a PainterlyGrassMesh is assigned. */
    [Export(PropertyHint.Range, "0.1,1.5,0.05")] public float GrassBladeHeight = 0.28f;

    /** How far blades may bleed past an edge toward a grass neighbour (fraction of HexRadius). Hides grass-grass seams; 0 = clip exactly to the hex. */
    [Export(PropertyHint.Range, "0.0,0.4,0.01")] public float GrassEdgeOverlap = 0.12f;

    /** Per-blade random height variation (± fraction). Higher = more varied blade heights. */
    [Export(PropertyHint.Range, "0,0.6,0.05")] public float GrassHeightJitter = 0.3f;

    /** Per-blade random width variation (± fraction). Higher = more varied blade widths. */
    [Export(PropertyHint.Range, "0,0.6,0.05")] public float GrassWidthJitter = 0.2f;

    /** Also scatter grass on Forest tiles (with a denser blade count). Off = grass on Grass tiles only. */
    [Export] public bool GrassOnForest = true;

    /** 0 = even meadow; 1 = grass fully driven by the clump field (dense clumps + thin gaps). */
    [Export(PropertyHint.Range, "0,1,0.05")] public float GrassClumpInfluence = 0.0f;

    /** World-space frequency of the clump field. Lower = bigger clumps and bigger bare patches. Flowers/rocks share this field, so changing it shifts where they cluster too. */
    [Export] public float GrassClumpFrequency = 0.35f;

    /** Clump-noise value below which ground is left fully bare (hard pockets). 0 = no hard bare spots. */
    [Export(PropertyHint.Range, "0,1,0.02")] public float GrassBareThreshold = 0.0f;

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

        Mesh bladeMesh = PainterlyGrassMesh ?? BuildProceduralBladeMesh(GrassBladeHeight);

        float densityScalar = DensityPreset switch
        {
            MapDensityPreset.Sparse => 0.5f,
            MapDensityPreset.Standard => 1.0f,
            MapDensityPreset.Dense => 1.4f,
            MapDensityPreset.Wild => 1.8f,
            _ => 1.0f
        };

        var rng = new RandomNumberGenerator { Seed = (ulong)(MapSeed ^ 0x6B61736D) };

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

        var transforms = new List<Transform3D>(Tiles.Count * GrassBladesPerTile);
        var nbrGrass = new bool[6];

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null || tile.IsBlocked || !IsGrassy(tile))
                continue;

            for (int k = 0; k < 6; k++)
            {
                var nc = tile.Axial + HexDirs[k];
                nbrGrass[k] = Tiles.TryGetValue(nc, out var nt)
                              && nt.TileView != null && !nt.IsBlocked && IsGrassy(nt);
            }

            bool isForest = tile.TerrainType == TileTerrainType.Forest;
            int baseCount = isForest
                ? Mathf.RoundToInt(GrassBladesPerTile * 1.35f)
                : GrassBladesPerTile;
            int count = Mathf.Max(0, Mathf.RoundToInt(baseCount * densityScalar));

            // X/Z come from the tile centre; Y is sampled per-blade below so
            // the carpet hugs the blended mesh instead of forming a flat shelf.
            Vector3 top = tile.TileView.GlobalPosition;

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

                float yaw = rng.RandfRange(0f, Mathf.Tau);
                float hs = GrassScale * (1f + rng.RandfRange(-GrassHeightJitter, GrassHeightJitter));
                float ws = GrassScale * (1f + rng.RandfRange(-GrassWidthJitter, GrassWidthJitter));

                var basis = new Basis(Vector3.Up, yaw)
                    .Scaled(new Vector3(Mathf.Max(0.05f, ws), Mathf.Max(0.05f, hs), Mathf.Max(0.05f, ws)));
                transforms.Add(new Transform3D(basis, pos));
            }
        }

        if (transforms.Count == 0)
            return;

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = bladeMesh,
            InstanceCount = transforms.Count
        };
        for (int i = 0; i < transforms.Count; i++)
            mm.SetInstanceTransform(i, transforms[i]);

        var mmi = new MultiMeshInstance3D
        {
            Name = "PainterlyGrassField",
            Multimesh = mm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        mmi.AddToGroup(PainterlyGrassGroup);

        Node parent = PropParent ?? this;
        parent.AddChild(mmi);
        mmi.GlobalTransform = Transform3D.Identity; // instance transforms are world-space
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
