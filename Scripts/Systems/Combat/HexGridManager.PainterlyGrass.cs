using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.PainterlyGrass.cs  (partial of HexGridManager)
//
// Animated ground-cover grass over grass/forest tiles. Now scatters
// across the FULL hexagon (so adjacent grass tiles tessellate into a
// continuous carpet reaching every edge), with grass-aware overlap:
// blades may bleed slightly past an edge ONLY toward a grass neighbour,
// hiding the seam; edges facing non-grass / the map boundary clip hard.
//
// INTEGRATION: one line at the tail of GenerateMap(), after
//   SpawnTerrainPropsFromManifest();
//       SpawnPainterlyGrass();
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Painterly Grass")]
    [Export] public bool EnablePainterlyGrass = true;
    [Export] public Shader PainterlyGrassShader;
    [Export] public string PainterlyGrassShaderPath = "res://Assets/Shaders/painterly_grass.gdshader";
    [Export] public Material PainterlyGrassMaterial;
    [Export] public Mesh PainterlyGrassMesh;

    [Export(PropertyHint.Range, "0.05,3.0,0.05")] public float GrassScale = 1.0f;
    [Export(PropertyHint.Range, "0,400,1")] public int GrassBladesPerTile = 40;
    [Export(PropertyHint.Range, "0.1,1.5,0.05")] public float GrassBladeHeight = 0.28f;

    /// <summary>How far blades may bleed past an edge toward a grass neighbour (fraction of HexRadius). Hides grass-grass seams; 0 = clip exactly to the hex.</summary>
    [Export(PropertyHint.Range, "0.0,0.4,0.01")] public float GrassEdgeOverlap = 0.12f;

    [Export(PropertyHint.Range, "0,0.6,0.05")] public float GrassHeightJitter = 0.3f;
    [Export(PropertyHint.Range, "0,0.6,0.05")] public float GrassWidthJitter = 0.2f;
    [Export] public bool GrassOnForest = true;

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

    /// <summary>
    /// Per-blade surface height, sampled the same way props are (analytic
    /// blended-mesh sample) so grass and props sit at identical heights on
    /// slopes. Legacy mode falls back to the flat tile top.
    /// </summary>
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
