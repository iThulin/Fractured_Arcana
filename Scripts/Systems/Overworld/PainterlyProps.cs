using Godot;

// ============================================================
// PainterlyProps.cs — art pass A5 (2026-08-12)
//
// Purpose:        Procedural painterly decoration meshes for the
//                 3D world-map renderers — canopy blob clusters
//                 replacing the stage-1 cone primitives. Shared by
//                 WorldAtlas3D and ExpeditionWindow3D (the
//                 Hex3DPalette/PainterlyPrism rule: one home, the
//                 two views can never drift). Meshes are built
//                 once via SurfaceTool.AppendFrom (merged sphere
//                 blobs → a single ArrayMesh, MultiMesh-friendly)
//                 and cached statically.
// Conventions:    Canopy meshes are authored BASE-AT-Y=0 (place at
//                 ground height directly). PeakCone keeps the old
//                 CylinderMesh CENTER origin — its callers' maths
//                 predate this file and were left untouched.
// Layer:          UI (rendering support)
// Collaborators:  WorldAtlas3D.RebuildDecorations,
//                 ExpeditionWindow3D.RebuildDecorations
// ============================================================

/// <summary>Procedural painterly prop meshes (map scale). Instance colour carries
/// the per-prop tint — the meshes have no vertex colours, so no multiply trap.</summary>
public static class PainterlyProps
{
    private static ArrayMesh _broadleaf;
    private static ArrayMesh _conifer;
    private static CylinderMesh _peak;

    /// <summary>Ghibli forest mound: three overlapping flattened blobs, ~1.0 wide,
    /// ~0.85 tall, base at y = 0. No trunk — at map zoom a forest reads as canopy
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
    /// ~1.1 tall, base at y = 0 — a soft pine silhouette, not a traffic cone.</summary>
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

    /// <summary>The stage-1 peak/spire cone, unchanged (CENTER origin — callers'
    /// placement maths predate this factory). Peaks still read correctly as rocky
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

    private static StandardMaterial3D _mat;

    /// <summary>Matte, instance-coloured — gouache paint, same register as the A4
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
    /// (smooth blobs); low segment counts keep the silhouette soft but cheap —
    /// these instance in the thousands.</summary>
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
