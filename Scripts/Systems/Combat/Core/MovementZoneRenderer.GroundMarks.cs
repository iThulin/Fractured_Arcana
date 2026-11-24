using Godot;
using System.Collections.Generic;

// ============================================================
// MovementZoneRenderer.GroundMarks.cs  (partial)
//
// Purpose:        Readability stage R1: ground that acts on a unit
//                 carries a drawn mark, so it can be told apart from
//                 ground that only looks like it. One pattern per
//                 element, in one neutral ink with a pale halo, so
//                 the mark reads on any palette and never borrows a
//                 colour that means something else.
//                   Frost      three slide streaks
//                   Fire       three flame teeth
//                   Lightning  one bolt
//                   Earth      a square
// Layer:          Combat / rendering
// Collaborators:  CombatManager.Outcome.cs (calls RefreshGroundMarks
//                   on its slow tick),
//                 MovementZoneRenderer.cs (AxialToWorld2D, HexRadius),
//                 UITheme.cs (GroundMarkInk, GroundMarkHalo)
// See:            docs/readability_style_guide_v1.md, rule 2
//
// The marks are rebuilt only when the set of (tile, element) pairs
// changes, so calling this five times a second costs one pass over
// the tiles and nothing else.
// ============================================================

public partial class MovementZoneRenderer : Node3D
{
    /// <summary>How far above the tile top the marks are drawn. Raise it if the
    /// terrain surface swallows them.</summary>
    [Export] public float GroundMarkLift = 0.06f;

    /// <summary>Stroke width as a fraction of the hex radius.</summary>
    [Export] public float GroundMarkStroke = 0.055f;

    /// <summary>Halo width as a fraction of the hex radius. Drawn under the ink.</summary>
    [Export] public float GroundMarkHaloStroke = 0.115f;

    private ImmediateMesh _groundMarkMesh;
    private MeshInstance3D _groundMarkInstance;
    private StandardMaterial3D _groundMarkMaterial;
    private int _groundMarkSignature = int.MinValue;

    /// <summary>Redraws the element marks if any tile's element changed since the last
    /// call. Safe to call often and before the grid has tiles.</summary>
    public void RefreshGroundMarks(HexGridManager grid)
    {
        if (grid == null || grid.Tiles == null)
            return;

        int signature = 17;
        int marked = 0;
        foreach (var td in grid.Tiles.Values)
        {
            if (td == null || !HasGroundMark(td.ElementType))
                continue;
            marked++;
            unchecked
            {
                signature = signature * 31 + td.Axial.X;
                signature = signature * 31 + td.Axial.Y;
                signature = signature * 31 + (int)td.ElementType;
                signature = signature * 31 + td.Height;
            }
        }
        if (signature == _groundMarkSignature)
            return;
        _groundMarkSignature = signature;
        _grid = grid;

        if (_groundMarkMesh == null)
        {
            _groundMarkMesh = new ImmediateMesh();
            // Depth-tested, so a unit standing on the tile covers the mark under it.
            _groundMarkMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = false,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _groundMarkInstance = new MeshInstance3D
            {
                Mesh = _groundMarkMesh,
                Name = "GroundMarkMesh",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_groundMarkInstance);
        }

        _groundMarkMesh.ClearSurfaces();
        if (marked == 0)
            return;

        _groundMarkMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _groundMarkMaterial);
        float halo = HexRadius * GroundMarkHaloStroke;
        float ink = HexRadius * GroundMarkStroke;

        // Halo pass first, then ink, so the ink always sits on top of its own halo.
        foreach (var td in grid.Tiles.Values)
        {
            if (td == null || !HasGroundMark(td.ElementType))
                continue;
            DrawGroundMark(td, halo, UITheme.GroundMarkHalo, GroundMarkLift);
        }
        foreach (var td in grid.Tiles.Values)
        {
            if (td == null || !HasGroundMark(td.ElementType))
                continue;
            DrawGroundMark(td, ink, UITheme.GroundMarkInk, GroundMarkLift + 0.005f);
        }
        _groundMarkMesh.SurfaceEnd();
    }

    /// <summary>Removes every ground mark (combat teardown).</summary>
    public void ClearGroundMarks()
    {
        _groundMarkMesh?.ClearSurfaces();
        _groundMarkSignature = int.MinValue;
    }

    private static bool HasGroundMark(TileElementType element) =>
        element == TileElementType.Frost || element == TileElementType.Fire
        || element == TileElementType.Lightning || element == TileElementType.Earth;

    private void DrawGroundMark(TileData td, float width, Color color, float lift)
    {
        Vector2 c = AxialToWorld2D(td.Axial);
        float y = td.Height * 0.5f + lift;
        float r = HexRadius;

        switch (td.ElementType)
        {
            case TileElementType.Frost:
                {
                    // Three parallel streaks: something slid through here.
                    var dir = new Vector2(0.94f, -0.34f);
                    var perp = new Vector2(0.34f, 0.94f);
                    for (int k = -1; k <= 1; k++)
                    {
                        float half = k == 0 ? 0.34f : 0.24f;
                        Vector2 mid = c + perp * (0.20f * k * r);
                        GroundStroke(mid - dir * (half * r), mid + dir * (half * r), y, width, color);
                    }
                }
                break;

            case TileElementType.Fire:
                {
                    // Three teeth: flame.
                    for (int k = -1; k <= 1; k++)
                    {
                        Vector2 b = c + new Vector2(0.26f * k * r, 0f);
                        Vector2 left = b + new Vector2(-0.10f * r, 0.12f * r);
                        Vector2 apex = b + new Vector2(0f, -0.14f * r);
                        Vector2 right = b + new Vector2(0.10f * r, 0.12f * r);
                        GroundStroke(left, apex, y, width, color);
                        GroundStroke(apex, right, y, width, color);
                    }
                }
                break;

            case TileElementType.Lightning:
                {
                    // One bolt.
                    Vector2 p0 = c + new Vector2(-0.30f * r, 0.16f * r);
                    Vector2 p1 = c + new Vector2(-0.06f * r, -0.10f * r);
                    Vector2 p2 = c + new Vector2(0.04f * r, 0.08f * r);
                    Vector2 p3 = c + new Vector2(0.30f * r, -0.18f * r);
                    GroundStroke(p0, p1, y, width, color);
                    GroundStroke(p1, p2, y, width, color);
                    GroundStroke(p2, p3, y, width, color);
                }
                break;

            case TileElementType.Earth:
                {
                    // A square: this ground holds.
                    float h = 0.20f * r;
                    Vector2 a = c + new Vector2(-h, -h);
                    Vector2 b = c + new Vector2(h, -h);
                    Vector2 d = c + new Vector2(h, h);
                    Vector2 e = c + new Vector2(-h, h);
                    GroundStroke(a, b, y, width, color);
                    GroundStroke(b, d, y, width, color);
                    GroundStroke(d, e, y, width, color);
                    GroundStroke(e, a, y, width, color);
                }
                break;
        }
    }

    /// <summary>One flat stroke on the ground from a to b (world X and Z), as two triangles.</summary>
    private void GroundStroke(Vector2 a, Vector2 b, float y, float width, Color color)
    {
        Vector2 along = b - a;
        if (along.LengthSquared() < 0.000001f)
            return;
        along = along.Normalized();
        var side = new Vector2(-along.Y, along.X) * (width * 0.5f);
        // Run the stroke half a width past each end so joined strokes close their corners.
        Vector2 a2 = a - along * (width * 0.5f);
        Vector2 b2 = b + along * (width * 0.5f);

        var v0 = new Vector3(a2.X - side.X, y, a2.Y - side.Y);
        var v1 = new Vector3(a2.X + side.X, y, a2.Y + side.Y);
        var v2 = new Vector3(b2.X + side.X, y, b2.Y + side.Y);
        var v3 = new Vector3(b2.X - side.X, y, b2.Y - side.Y);

        _groundMarkMesh.SurfaceSetColor(color);
        _groundMarkMesh.SurfaceAddVertex(v0);
        _groundMarkMesh.SurfaceSetColor(color);
        _groundMarkMesh.SurfaceAddVertex(v1);
        _groundMarkMesh.SurfaceSetColor(color);
        _groundMarkMesh.SurfaceAddVertex(v2);

        _groundMarkMesh.SurfaceSetColor(color);
        _groundMarkMesh.SurfaceAddVertex(v0);
        _groundMarkMesh.SurfaceSetColor(color);
        _groundMarkMesh.SurfaceAddVertex(v2);
        _groundMarkMesh.SurfaceSetColor(color);
        _groundMarkMesh.SurfaceAddVertex(v3);
    }
}
