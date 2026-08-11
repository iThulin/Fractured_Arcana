using Godot;
using System.Collections.Generic;

// ============================================================
// MovementZoneRenderer.Objective.cs  (partial)
//
// Purpose:        Persistent OBJECTIVE-ZONE overlay — the "hold this
//                 ground" indicator for hold_zone fights (O4, city
//                 siege gate defense). Same XCOM border-wall visual
//                 grammar as the movement/threat zones, but on its OWN
//                 mesh + material so ShowPlayerZone/Clear churn from
//                 selection changes never erases it. Gold, solid (no
//                 dash animation — it is a fact, not a preview), and
//                 NoDepthTest so it stays readable behind the
//                 gatehouse shell.
// Layer:          Combat / rendering
// Collaborators:  CombatManager.Objectives (caller),
//                 MovementZoneRenderer.cs (geometry helpers:
//                 AxialToWorld2D, HexCorner, EdgeForDir, HexDirs)
// ============================================================

public partial class MovementZoneRenderer : Node3D
{
    /// <summary>Objective-zone border colour. Gold — deliberately outside the
    /// blue/red movement/threat vocabulary.</summary>
    [Export] public Color ObjectiveColor = new Color(1.00f, 0.78f, 0.22f, 0.85f);

    /// <summary>Objective wall height — between the player lip (0.1) and the
    /// threat walls (0.6): present, not looming.</summary>
    [Export] public float ObjectiveWallHeight = 0.28f;

    private ImmediateMesh _objectiveMesh;
    private MeshInstance3D _objectiveInstance;
    private StandardMaterial3D _objectiveMaterial;

    /// <summary>Draws the persistent border around the objective tiles.
    /// Static geometry, built once per call; safe to re-call. Creates its
    /// node lazily (this is invoked mid-combat, well after _Ready).</summary>
    public void ShowObjectiveZone(HashSet<Vector2I> tiles, HexGridManager grid)
    {
        if (tiles == null || tiles.Count == 0 || grid == null)
            return;

        _grid = grid;

        if (_objectiveMesh == null)
        {
            _objectiveMesh = new ImmediateMesh();
            _objectiveMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _objectiveInstance = new MeshInstance3D
            {
                Mesh = _objectiveMesh,
                Name = "ObjectiveZoneMesh",
            };
            AddChild(_objectiveInstance);
        }

        _objectiveMesh.ClearSurfaces();
        _objectiveMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _objectiveMaterial);

        var bottomCol = new Color(ObjectiveColor.R, ObjectiveColor.G, ObjectiveColor.B,
                                  ObjectiveColor.A);
        var topCol = new Color(ObjectiveColor.R, ObjectiveColor.G, ObjectiveColor.B, 0.10f);

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
                    continue;   // interior edge — no border here

                int edge = EdgeForDir[d];
                var cA = center2D + HexCorner(edge);
                var cB = center2D + HexCorner((edge + 1) % 6);

                var a0 = new Vector3(cA.X, tileY, cA.Y);
                var b0 = new Vector3(cB.X, tileY, cB.Y);
                var a1 = new Vector3(cA.X, tileY + ObjectiveWallHeight, cA.Y);
                var b1 = new Vector3(cB.X, tileY + ObjectiveWallHeight, cB.Y);

                // solid quad, bright base fading upward
                _objectiveMesh.SurfaceSetColor(bottomCol);
                _objectiveMesh.SurfaceAddVertex(a0);
                _objectiveMesh.SurfaceSetColor(bottomCol);
                _objectiveMesh.SurfaceAddVertex(b0);
                _objectiveMesh.SurfaceSetColor(topCol);
                _objectiveMesh.SurfaceAddVertex(a1);

                _objectiveMesh.SurfaceSetColor(topCol);
                _objectiveMesh.SurfaceAddVertex(a1);
                _objectiveMesh.SurfaceSetColor(bottomCol);
                _objectiveMesh.SurfaceAddVertex(b0);
                _objectiveMesh.SurfaceSetColor(topCol);
                _objectiveMesh.SurfaceAddVertex(b1);
            }
        }

        _objectiveMesh.SurfaceEnd();
    }

    /// <summary>Removes the objective overlay (combat teardown / next fight).</summary>
    public void ClearObjectiveZone()
    {
        _objectiveMesh?.ClearSurfaces();
    }
}
