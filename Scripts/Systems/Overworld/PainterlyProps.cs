using Godot;

// ============================================================
// PainterlyProps.cs (art pass A5, 2026-08-12)
//
// Purpose:        Procedural painterly decoration meshes for the
//                 3D world-map renderers: canopy blob clusters
//                 replacing the stage-1 cone primitives. Shared by
//                 WorldAtlas3D and ExpeditionWindow3D (the
//                 Hex3DPalette/PainterlyPrism rule: one home, the
//                 two views can never drift). Meshes are built
//                 once via SurfaceTool.AppendFrom (merged sphere
//                 blobs → a single ArrayMesh, MultiMesh-friendly)
//                 and cached statically.
// Conventions:    Canopy meshes are authored BASE-AT-Y=0 (place at
//                 ground height directly). PeakCone keeps the old
//                 CylinderMesh CENTER origin, because its callers' maths
//                 predate this file and were left untouched.
// Layer:          UI (rendering support)
// Collaborators:  WorldAtlas3D.RebuildDecorations,
//                 ExpeditionWindow3D.RebuildDecorations
// ============================================================

/// <summary>Procedural painterly prop meshes (map scale). Instance colour carries
/// the per-prop tint. The meshes have no vertex colours, so no multiply trap.</summary>
public static class PainterlyProps
{
    private static ArrayMesh _broadleaf;
    private static ArrayMesh _conifer;
    private static CylinderMesh _peak;

    /// <summary>Ghibli forest mound: three overlapping flattened blobs, ~1.0 wide,
    /// ~0.85 tall, base at y = 0. No trunk: at map zoom a forest reads as canopy
    /// mass, and one mesh keeps the whole set a single MultiMesh layer.</summary>
    public static ArrayMesh BroadleafCanopy()
    {
        if (_broadleaf != null) return _broadleaf;
        _broadleaf = Blobs(new (Vector3 pos, Vector3 scale)[]
        {
            (new Vector3(0f, 0.42f, 0f), new Vector3(0.90f, 0.66f, 0.90f)),
            (new Vector3(0.27f, 0.34f, 0.10f), new Vector3(0.58f, 0.46f, 0.58f)),
            (new Vector3(-0.24f, 0.37f, -0.12f), new Vector3(0.52f, 0.42f, 0.52f)),
        });
        _broadleaf.SurfaceSetMaterial(0, PropMaterial());
        return _broadleaf;
    }

    /// <summary>Bushy conifer: three stacked squashed blobs tapering upward,
    /// ~1.1 tall, base at y = 0. A soft pine silhouette, not a traffic cone.</summary>
    public static ArrayMesh ConiferCanopy()
    {
        if (_conifer != null) return _conifer;
        _conifer = Blobs(new (Vector3 pos, Vector3 scale)[]
        {
            (new Vector3(0f, 0.24f, 0f), new Vector3(0.68f, 0.48f, 0.68f)),
            (new Vector3(0f, 0.58f, 0f), new Vector3(0.48f, 0.44f, 0.48f)),
            (new Vector3(0f, 0.88f, 0f), new Vector3(0.28f, 0.38f, 0.28f)),
        });
        _conifer.SurfaceSetMaterial(0, PropMaterial());
        return _conifer;
    }

    /// <summary>The stage-1 peak/spire cone, unchanged (CENTER origin, because
    /// callers' placement maths predate this factory). Peaks still read correctly as rocky
    /// spires at map zoom; replace with authored crags in a later pass if wanted.</summary>
    public static CylinderMesh PeakCone(float baseRadius, float height)
    {
        if (_peak != null) return _peak;
        _peak = new CylinderMesh
        {
            TopRadius = 0f, BottomRadius = baseRadius, Height = height,
            RadialSegments = 5, Rings = 0,
        };
        _peak.Material = PropMaterial();
        return _peak;
    }

    private static ArrayMesh _hexTile;

    /// <summary>A hex tile prism with a SUBDIVIDED top (centre + mid ring + rim)
    /// so the prism shader's <c>top_undulation</c> can roll the ground surface.
    /// This is stage 1 of the expedition window's terrain break-up. Matches CylinderMesh
    /// conventions exactly (unit height ±0.5, x = sin/z = cos corner phase, no
    /// bottom cap) so it drops into the same per-instance transforms; flat
    /// outward wall normals keep the carved-facet read. Windings follow the
    /// project's CW-front rule (verified by the RH-normal sign test).
    /// NOTE: cached, so the taper argument is honoured on first call only.</summary>
    public static ArrayMesh HexTileMesh(float taper)
    {
        if (_hexTile != null) return _hexTile;

        var corner = new Vector3[7];
        var rim = new Vector3[7];
        var mid = new Vector3[7];
        var bottom = new Vector3[7];
        for (int i = 0; i < 7; i++)
        {
            float ang = (i % 6) * Mathf.Tau / 6f;
            var dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
            corner[i] = dir;
            rim[i] = dir * taper + Vector3.Up * 0.5f;
            mid[i] = dir * (taper * 0.5f) + Vector3.Up * 0.5f;
            bottom[i] = dir - Vector3.Up * 0.5f;
        }
        var centre = Vector3.Up * 0.5f;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < 6; i++)
        {
            // Top: centre fan + mid→rim band, smooth up normals.
            TopTri(st, centre, mid[i + 1], mid[i]);
            TopTri(st, mid[i], mid[i + 1], rim[i + 1]);
            TopTri(st, mid[i], rim[i + 1], rim[i]);
            // Wall: flat outward normal.
            Vector3 n = (corner[i] + corner[i + 1]).Normalized();
            WallTri(st, n, rim[i], rim[i + 1], bottom[i + 1]);
            WallTri(st, n, rim[i], bottom[i + 1], bottom[i]);
        }
        _hexTile = st.Commit();
        return _hexTile;
    }

    private static void TopTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
    {
        st.SetNormal(Vector3.Up); st.AddVertex(a);
        st.SetNormal(Vector3.Up); st.AddVertex(b);
        st.SetNormal(Vector3.Up); st.AddVertex(c);
    }

    private static void WallTri(SurfaceTool st, Vector3 n, Vector3 a, Vector3 b, Vector3 c)
    {
        st.SetNormal(n); st.AddVertex(a);
        st.SetNormal(n); st.AddVertex(b);
        st.SetNormal(n); st.AddVertex(c);
    }

    /// <summary>Surface index of the banner's pennant. Recolour a planted banner
    /// via <c>MeshInstance3D.SetSurfaceOverrideMaterial(BannerFlagSurface, mat)</c>;
    /// the pole material is baked on surface 0.</summary>
    public const int BannerFlagSurface = 1;
    private static ArrayMesh _banner;

    /// <summary>A planted standard (art pass A7): wooden pole + gently waving
    /// swallow pennant, base at y = 0, ~3.3 tall. Replaces the cone beacons.
    /// The pennant emits BOTH windings so the flag reads from every side
    /// regardless of the override material's cull mode.</summary>
    public static ArrayMesh Banner()
    {
        if (_banner != null) return _banner;

        // Surface 0: the pole.
        var pole = new CylinderMesh
        {
            TopRadius = 0.045f, BottomRadius = 0.06f, Height = 3.3f,
            RadialSegments = 6, Rings = 0,
        };
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.AppendFrom(pole, 0, new Transform3D(Basis.Identity, new Vector3(0f, 1.65f, 0f)));
        _banner = st.Commit();
        _banner.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.32f, 0.25f, 0.19f),
            Roughness = 0.9f,
        });

        // Surface 1: the pennant, two quads with a gentle Z wave and tip droop.
        var fst = new SurfaceTool();
        fst.Begin(Mesh.PrimitiveType.Triangles);
        var rootTop = new Vector3(0.05f, 3.10f, 0f);
        var rootBot = new Vector3(0.05f, 2.50f, 0f);
        var midTop = new Vector3(0.60f, 3.06f, 0.09f);
        var midBot = new Vector3(0.60f, 2.52f, 0.09f);
        var tipTop = new Vector3(1.10f, 2.99f, 0.18f);
        var tipBot = new Vector3(1.10f, 2.57f, 0.18f);
        FlagQuad(fst, rootTop, midTop, midBot, rootBot);
        FlagQuad(fst, midTop, tipTop, tipBot, midBot);
        fst.Commit(_banner);   // appends as surface 1
        _banner.SurfaceSetMaterial(BannerFlagSurface, PropMaterial());   // safe default; instances override
        return _banner;
    }

    private static void FlagQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 n = (b - a).Cross(d - a);
        n = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Back;
        st.SetNormal(n); st.AddVertex(a);
        st.SetNormal(n); st.AddVertex(b);
        st.SetNormal(n); st.AddVertex(c);
        st.SetNormal(n); st.AddVertex(a);
        st.SetNormal(n); st.AddVertex(c);
        st.SetNormal(n); st.AddVertex(d);
        Vector3 m = -n;
        st.SetNormal(m); st.AddVertex(a);
        st.SetNormal(m); st.AddVertex(c);
        st.SetNormal(m); st.AddVertex(b);
        st.SetNormal(m); st.AddVertex(a);
        st.SetNormal(m); st.AddVertex(d);
        st.SetNormal(m); st.AddVertex(c);
    }

    private static StandardMaterial3D _mat;

    /// <summary>Matte and instance-coloured: gouache paint, same register as the A4
    /// tile materials. One shared instance.</summary>
    private static StandardMaterial3D PropMaterial()
    {
        _mat ??= new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.9f,
        };
        return _mat;
    }

    /// <summary>Merge low-poly spheres into one ArrayMesh. Sphere normals are kept
    /// (smooth blobs); low segment counts keep the silhouette soft but cheap,
    /// since these instance in the thousands.</summary>
    private static ArrayMesh Blobs((Vector3 pos, Vector3 scale)[] parts)
    {
        var sphere = new SphereMesh
        {
            Radius = 0.5f, Height = 1f,
            RadialSegments = 8, Rings = 5,
        };
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var (pos, scale) in parts)
            st.AppendFrom(sphere, 0, new Transform3D(Basis.FromScale(scale), pos));
        return st.Commit();
    }
}
