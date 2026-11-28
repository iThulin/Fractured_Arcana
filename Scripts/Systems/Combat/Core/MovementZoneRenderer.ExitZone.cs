using Godot;
using System.Collections.Generic;

// ============================================================
// MovementZoneRenderer.ExitZone.cs  (partial)
//
// Purpose:        Persistent EXIT-ZONE overlay: the tiles a unit
//                 stands on to withdraw from the fight (stage C5).
//                 Same border-wall grammar as the objective zone, on
//                 its own mesh and material so selection churn and
//                 the objective overlay never erase it. Green, low
//                 and solid: a fact about the board, not a preview.
// Layer:          Combat / rendering
// Collaborators:  CombatManager.Outcome.cs (caller),
//                 MovementZoneRenderer.cs (geometry helpers:
//                 AxialToWorld2D, HexCorner, EdgeForDir, HexDirs)
// See:            docs/combat_c5_edge_withdraw_v1.md
// ============================================================

public partial class MovementZoneRenderer : Node3D
{
    /// <summary>Exit-zone border colour. Green, outside the blue and red of
    /// movement and threat and the gold of the objective.</summary>
    [Export] public Color ExitColor = new Color(0.45f, 0.92f, 0.62f, 0.80f);

    /// <summary>Exit wall height. Lower than the objective wall (0.28) so an
    /// objective drawn on the same tiles still reads on top.</summary>
    [Export] public float ExitWallHeight = 0.16f;

    private ImmediateMesh _exitMesh;
    private MeshInstance3D _exitInstance;
    private StandardMaterial3D _exitMaterial;

    /// <summary>Draws the border around the exit tiles. Static geometry, built
    /// once per call. Safe to call again with a different set.</summary>
    public void ShowExitZone(HashSet<Vector2I> tiles, HexGridManager grid)
    {
        if (tiles == null || tiles.Count == 0 || grid == null)
            return;

        _grid = grid;

        if (_exitMesh == null)
        {
            _exitMesh = new ImmediateMesh();
            _exitMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _exitInstance = new MeshInstance3D
            {
                Mesh = _exitMesh,
                Name = "ExitZoneMesh",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_exitInstance);
        }

        _exitMesh.ClearSurfaces();
        _exitMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _exitMaterial);

        var bottomCol = ExitColor;
        var topCol = new Color(ExitColor.R, ExitColor.G, ExitColor.B, 0.08f);

        foreach (var coord in tiles)
        {
            float tileY = 0.02f;
            var td = grid.GetTile(coord);
            if (td != null)
                tileY = td.Height * 0.5f + 0.02f;

            var center2D = AxialToWorld2D(coord);

            for (int d = 0; d < 6; d++)
            {
                if (tiles.Contains(coord + HexDirs[d]))
                    continue;   // interior edge: no border here

                int edge = EdgeForDir[d];
                var cA = center2D + HexCorner(edge);
                var cB = center2D + HexCorner((edge + 1) % 6);

                var a0 = new Vector3(cA.X, tileY, cA.Y);
                var b0 = new Vector3(cB.X, tileY, cB.Y);
                var a1 = new Vector3(cA.X, tileY + ExitWallHeight, cA.Y);
                var b1 = new Vector3(cB.X, tileY + ExitWallHeight, cB.Y);

                _exitMesh.SurfaceSetColor(bottomCol);
                _exitMesh.SurfaceAddVertex(a0);
                _exitMesh.SurfaceSetColor(bottomCol);
                _exitMesh.SurfaceAddVertex(b0);
                _exitMesh.SurfaceSetColor(topCol);
                _exitMesh.SurfaceAddVertex(a1);

                _exitMesh.SurfaceSetColor(topCol);
                _exitMesh.SurfaceAddVertex(a1);
                _exitMesh.SurfaceSetColor(bottomCol);
                _exitMesh.SurfaceAddVertex(b0);
                _exitMesh.SurfaceSetColor(topCol);
                _exitMesh.SurfaceAddVertex(b1);
            }
        }

        _exitMesh.SurfaceEnd();
    }

    /// <summary>Removes the exit overlay (the fight ended, or Withdraw is not offered).</summary>
    public void ClearExitZone()
    {
        _exitMesh?.ClearSurfaces();
    }
}
