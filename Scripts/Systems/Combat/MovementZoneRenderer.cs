using Godot;
using System.Collections.Generic;

// ══════════════════════════════════════════════════════════════════════════════
// MovementZoneRenderer
//
// Draws an XCOM-style animated border outline around a set of reachable tiles.
// For each reachable tile, checks all 6 edges. If the neighbor across that edge
// is NOT in the reachable set (or doesn't exist), that edge is a border edge and
// gets a line segment drawn.
//
// Also handles enemy threat zone display when hovering an enemy unit.
//
// Add as a child of the HexGridManager node in your combat scene.
// ══════════════════════════════════════════════════════════════════════════════
public partial class MovementZoneRenderer : Node3D
{
    // ── Exports ───────────────────────────────────────────────────────────
    [Export] public float HexRadius = 1.0f;   // match HexGridManager.HexRadius
    [Export] public float LineWidth = 0.08f;  // world-space width of border line
    [Export] public float LineHeight = 0.12f;  // Y offset above tile surface
    [Export] public float AnimSpeed = .35f;   // dash animation speed
    [Export] public float DashLength = 0.65f;  // fraction of each edge that is solid
    [Export] public Color PlayerColor = new Color(0.20f, 0.70f, 1.00f, 1.0f); // blue
    [Export] public Color EnemyColor = new Color(0.90f, 0.25f, 0.25f, 0.75f); // red

    // ── References ───────────────────────────────────────────────────────
    private HexGridManager _grid;

    // ── Cost label ────────────────────────────────────────────────────────
    private Label3D _costLabel;
    private Vector2I _lastHoveredTile = new Vector2I(int.MinValue, int.MinValue);

    // ── Runtime ───────────────────────────────────────────────────────────
    private MeshInstance3D _borderMesh;
    private ImmediateMesh _immediateMesh;
    private StandardMaterial3D _lineMaterial;

    private HashSet<Vector2I> _reachableSet = new();
    private Dictionary<Vector2I, int> _costMap = new();
    private bool _isPlayerZone = true;
    private float _animOffset = 0f;

    // Flat-top hex: the 6 edge midpoint offsets and their perpendicular directions
    // Edge i connects corner i and corner (i+1)%6
    // For a flat-top hex with radius R, corners are at angles 0,60,120,180,240,300 degrees
    private static readonly float[] CornerAngles = { 0f, 60f, 120f, 180f, 240f, 300f };

    // The 6 axial neighbor directions (same order as HexGridManager.HexDirs)
    private static readonly Vector2I[] HexDirs =
    {
        new Vector2I( 1,  0),
        new Vector2I( 1, -1),
        new Vector2I( 0, -1),
        new Vector2I(-1,  0),
        new Vector2I(-1,  1),
        new Vector2I( 0,  1),
    };

    // Which edge index (0-5) faces each neighbor direction.
    // Edge i is between corner i and corner (i+1)%6 in flat-top orientation.
    // Neighbor in direction HexDirs[d] shares edge EdgeForDir[d].
    private static readonly int[] EdgeForDir = { 0, 1, 2, 3, 4, 5 };

    public override void _Ready()
    {
        _immediateMesh = new ImmediateMesh();
        _lineMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _borderMesh = new MeshInstance3D
        {
            Mesh = _immediateMesh,
            MaterialOverride = _lineMaterial,  // ← works on empty mesh
        };
        AddChild(_borderMesh);

        // Cost label — hidden until hover
        _costLabel = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 28,
            Visible = false,
            Name = "CostLabel",
        };
        AddChild(_costLabel);
    }

    public override void _Process(double delta)
    {
        if (_reachableSet.Count == 0) return;
        _animOffset = (_animOffset + (float)delta * AnimSpeed) % 1.0f;
        RebuildMesh();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Show the movement zone for a player unit.</summary>
    public void ShowPlayerZone(Dictionary<Vector2I, int> costMap, HexGridManager grid)
    {
        _grid = grid;
        _reachableSet.Clear();
        _costMap = costMap;
        foreach (var k in costMap.Keys) _reachableSet.Add(k);
        _isPlayerZone = true;
        _lineMaterial.AlbedoColor = PlayerColor;
        HideCostLabel();
        RebuildMesh();
    }

    /// <summary>Show the threat zone for an enemy unit (hover preview).</summary>
    public void ShowEnemyZone(HashSet<Vector2I> reachable, HexGridManager grid)
    {
        _grid = grid;
        _reachableSet = reachable;
        _costMap.Clear();
        _isPlayerZone = false;
        _lineMaterial.AlbedoColor = EnemyColor;
        HideCostLabel();
        RebuildMesh();
    }

    /// <summary>Clear all zone display.</summary>
    public void Clear()
    {
        _reachableSet.Clear();
        _costMap.Clear();
        _immediateMesh.ClearSurfaces();
        HideCostLabel();
    }

    /// <summary>
    /// Show or update the cost label for a hovered tile.
    /// Pass null tile to hide it.
    /// </summary>
    public void ShowCostLabelForTile(Vector2I axial, HexGridManager grid, int baseSpeed)
    {
        if (!_reachableSet.Contains(axial)) { HideCostLabel(); return; }
        if (axial == _lastHoveredTile) return;
        _lastHoveredTile = axial;

        int stepCost = _costMap.TryGetValue(axial, out var c) ? c : -1;
        string label = stepCost < 0
            ? "1 AP"
            : $"1 AP  ({stepCost}/{baseSpeed} steps)";

        var tileData = grid.GetTile(axial);
        float tileY = tileData != null ? tileData.Height * 0.5f + 0.8f : 0.8f;
        var worldPos = grid.AxialToWorld(axial);
        worldPos.Y = tileY;

        _costLabel.Text = label;
        _costLabel.Position = worldPos;
        _costLabel.Modulate = _isPlayerZone ? PlayerColor : EnemyColor;
        _costLabel.Visible = true;
    }

    public void HideCostLabel()
    {
        _costLabel.Visible = false;
        _lastHoveredTile = new Vector2I(int.MinValue, int.MinValue);
    }

    // ── Mesh building ─────────────────────────────────────────────────────

    private void RebuildMesh()
    {
        _immediateMesh.ClearSurfaces();
        if (_reachableSet.Count == 0) return;

        // ── Pass 1: tile fill ─────────────────────────────────────────────
        var fillColor = _isPlayerZone
            ? new Color(PlayerColor.R, PlayerColor.G, PlayerColor.B, 0.18f)
            : new Color(EnemyColor.R, EnemyColor.G, EnemyColor.B, 0.15f);

        _immediateMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var coord in _reachableSet)
            DrawFilledHex(coord, fillColor);
        _immediateMesh.SurfaceEnd();

        // ── Pass 2: border outline ────────────────────────────────────────
        _immediateMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var coord in _reachableSet)
        {
            for (int d = 0; d < 6; d++)
            {
                var neighbor = coord + HexDirs[d];
                if (_reachableSet.Contains(neighbor)) continue;
                DrawEdge(coord, d);
            }
        }
        _immediateMesh.SurfaceEnd();
    }

    private void DrawFilledHex(Vector2I coord, Color color)
    {
        float tileY = LineHeight * 0.5f; // just above tile surface, below border
        if (_grid != null)
        {
            var tileData = _grid.GetTile(coord);
            if (tileData != null)
                tileY = tileData.Height * 0.5f + 0.02f;
        }

        var center2D = AxialToWorld2D(coord);
        var center3D = new Vector3(center2D.X, tileY, center2D.Y);

        for (int i = 0; i < 6; i++)
        {
            var cA = center2D + HexCorner(i);
            var cB = center2D + HexCorner((i + 1) % 6);

            var vA = new Vector3(cA.X, tileY, cA.Y);
            var vB = new Vector3(cB.X, tileY, cB.Y);

            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(center3D);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(vA);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(vB);
        }
    }

    private void DrawEdge(Vector2I coord, int neighborDir)
    {
        float tileY = LineHeight;
        if (_grid != null)
        {
            var tileData = _grid.GetTile(coord);
            if (tileData != null)
                tileY = tileData.Height * 0.5f + LineHeight;
        }

        var center2D = AxialToWorld2D(coord);

        // The two corners that form the edge facing neighbor direction neighborDir.
        // For flat-top hex with 30° offset, edge facing direction d
        // is between corners d and (d+1)%6.
        int cornerA = neighborDir;
        int cornerB = (neighborDir + 1) % 6;

        var cA = center2D + HexCorner(cornerA);
        var cB = center2D + HexCorner(cornerB);

        float edgeLen = cA.DistanceTo(cB);
        var start3D = new Vector3(cA.X, tileY, cA.Y);
        var end3D = new Vector3(cB.X, tileY, cB.Y);
        var edgeVec = (end3D - start3D).Normalized();
        var perpDir = new Vector3(-edgeVec.Z, 0, edgeVec.X);

        float dashWorldLen = DashLength * edgeLen;
        float cycleLen = edgeLen / Mathf.Max(1f, Mathf.Round(edgeLen / (dashWorldLen * 1.5f)));
        dashWorldLen = cycleLen * DashLength;

        float startOffset = (_animOffset * cycleLen * 2f) % cycleLen;
        float t = -startOffset;

        var color = _isPlayerZone ? PlayerColor : EnemyColor;

        while (t < edgeLen)
        {
            float dashStart = Mathf.Max(t, 0f);
            float dashEnd = Mathf.Min(t + dashWorldLen, edgeLen);

            if (dashEnd > dashStart + 0.001f)
            {
                var p1 = start3D + edgeVec * dashStart;
                var p2 = start3D + edgeVec * dashEnd;
                var offset = perpDir * (LineWidth * 0.5f);

                var v1 = p1 - offset;
                var v2 = p1 + offset;
                var v3 = p2 + offset;
                var v4 = p2 - offset;

                _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(v1);
                _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(v2);
                _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(v3);

                _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(v1);
                _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(v3);
                _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(v4);
            }

            t += cycleLen;
        }
    }

    // ── Coordinate helpers ────────────────────────────────────────────────

    /// <summary>Convert axial to 2D XZ world position (ignoring height).</summary>
    private Vector2 AxialToWorld2D(Vector2I coord)
    {
        float x = HexRadius * 1.5f * coord.X;
        float z = HexRadius * Mathf.Sqrt(3f) * (coord.Y + coord.X / 2f);
        return new Vector2(x, z);
    }

    /// <summary>
    /// Return the 2D (XZ) offset from hex center to corner i (0-5).
    /// Flat-top orientation: corner 0 is at angle 0° (right), proceeding CCW.
    /// </summary>
    private Vector2 HexCorner(int i)
    {
        float angleDeg = 60f * i;
        float angleRad = Mathf.DegToRad(angleDeg);
        return new Vector2(
            HexRadius * Mathf.Cos(angleRad),
            HexRadius * Mathf.Sin(angleRad)
        );
    }
}
