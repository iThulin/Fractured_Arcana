using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// TrajectoryTrace.cs
//
// Purpose:        The line a dragged card would travel from the caster
//                 to the hovered target. A Bolt draws straight along
//                 the hex line at chest height; an Arc draws a low lob
//                 that visibly clears a low wall. The line turns from
//                 the cast colour to red from the first blocker onward
//                 (or entirely red when the shot is impossible), so
//                 "why can't I hit him" is answered before the drop.
// Layer:          Systems / Combat / Core
// Collaborators:  CombatManager.CastPreview (Show / Clear),
//                 HexGridManager (AxialToWorld, tile heights)
// See:            docs/cover_and_zoc_v1.md §11
// ============================================================

public partial class TrajectoryTrace : Node3D
{
    public enum Style { Straight, Lob }

    [Export] public Color ClearColor = new(0.98f, 0.86f, 0.45f, 0.9f);
    [Export] public Color BlockedColor = new(0.90f, 0.30f, 0.25f, 0.9f);
    [Export] public float Width = 0.07f;
    [Export] public float ChestHeight = 0.9f;     // bolt height above the tile top
    [Export] public float LobHeight = 1.6f;       // arc apex above the higher endpoint
    [Export] public int Segments = 28;

    private MeshInstance3D _meshInstance;
    private readonly ImmediateMesh _mesh = new();
    private readonly StandardMaterial3D _mat = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        VertexColorUseAsAlbedo = true,
        NoDepthTest = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public override void _Ready()
    {
        _meshInstance = new MeshInstance3D { Mesh = _mesh, Name = "TraceMesh" };
        AddChild(_meshInstance);
    }

    public void Clear() => _mesh.ClearSurfaces();

    /// <summary>Draw from <paramref name="from"/> to <paramref name="to"/>.
    /// <paramref name="blockerCoord"/> is the first sight-blocking tile on the hex
    /// line (null when clear); <paramref name="blocked"/> says the cast cannot land
    /// at all (full cover, out of range, or a bolt with a blocker), which paints the
    /// whole trace red. An Arc with a blocker keeps its colour up to the blocker: the
    /// lob clears low cover but a wall on the line still stops the sight the card asks for.</summary>
    public void Show(HexGridManager grid, Vector2I from, Vector2I to, Style style, Vector2I? blockerCoord, bool blocked)
    {
        _mesh.ClearSurfaces();
        if (grid == null || from == to)
            return;

        var a = TopOf(grid, from) + new Vector3(0f, ChestHeight, 0f);
        var b = TopOf(grid, to) + new Vector3(0f, ChestHeight, 0f);

        // Where along the line the blocker sits, as a 0..1 fraction of the run.
        float blockT = 2f;
        if (blockerCoord.HasValue)
        {
            int total = Math.Max(1, grid.Distance(from, to));
            blockT = (float)grid.Distance(from, blockerCoord.Value) / total;
        }

        var camera = GetViewport()?.GetCamera3D();
        var viewDir = camera != null ? -camera.GlobalTransform.Basis.Z : Vector3.Up;

        _mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, _mat);
        for (int i = 0; i <= Segments; i++)
        {
            float t = (float)i / Segments;
            var p = a.Lerp(b, t);
            if (style == Style.Lob)
                p.Y = Mathf.Lerp(a.Y, b.Y, t) + LobHeight * 4f * t * (1f - t);   // parabola, 0 at the ends

            var color = (blocked || t >= blockT) ? BlockedColor : ClearColor;
            // Fade the tail so the line reads as flight, not a rod.
            color.A *= 0.45f + 0.55f * t;

            // Ribbon across the view direction so it faces the camera.
            var dir = (b - a).Normalized();
            var side = dir.Cross(viewDir).Normalized() * (Width * 0.5f);
            if (side.LengthSquared() < 1e-6f)
                side = Vector3.Right * (Width * 0.5f);

            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(p - side);
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(p + side);
        }
        _mesh.SurfaceEnd();
    }

    /// <summary>Tile top in GRID-LOCAL space. This node is a child of the grid, so
    /// its mesh is drawn in the same space AxialToWorld returns.</summary>
    private static Vector3 TopOf(HexGridManager grid, Vector2I coord)
    {
        var td = grid.GetTile(coord);
        if (td?.TileView != null)
            return td.TileView.Position;      // the tile node's origin is its top surface
        var w = grid.AxialToWorld(coord);
        w.Y = td != null ? td.Height * 0.5f : 0f;
        return w;
    }
}
