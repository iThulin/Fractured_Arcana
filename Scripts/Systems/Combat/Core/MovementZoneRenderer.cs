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

    // ── Tiered threat walls (2026-07-13) ──────────────────────────────────
    [Export] public float WallHeight = 0.6f;        // tallest tier ≈ half a unit's height
    [Export] public float WallBaseAlphaMin = 0.35f; // movement-only wall base alpha
    [Export] public float WallBaseAlphaMax = 0.70f; // multi-hit kill-zone wall base alpha
    [Export] public float SkirtVoidDepth = 1.0f;    // fill skirt depth at map-edge / void faces
    [Export] public float SkirtOutwardOffset = 0.03f; // push skirt off the riser (depth-tested; avoids z-fight)
    [Export] public float PlayerLipHeight = 0.1f;   // player move-zone edge = subtle lip, not a wall

    // ── References ───────────────────────────────────────────────────────
    private HexGridManager _grid;

    // ── Cost label ────────────────────────────────────────────────────────
    private Label3D _costLabel;
    private Vector2I _lastHoveredTile = new Vector2I(int.MinValue, int.MinValue);

    // ── Runtime ───────────────────────────────────────────────────────────
    private MeshInstance3D _borderMesh;

    // Resources created at construction, NOT in _Ready (2026-07-09): the
    // skip-deploy handoff calls Clear()/ShowPlayerZone() before this node has
    // entered the tree — round-1 StartPlayerTurn NRE'd at Clear() in every
    // version of the handoff (the root cause of the "first turn never
    // auto-selects" defect chain). Resources are tree-independent, so they are
    // safe to build here; the NODES (_borderMesh, _costLabel) still build in
    // _Ready and their uses are null-guarded.
    private ImmediateMesh _immediateMesh = new ImmediateMesh();
    private StandardMaterial3D _lineMaterial = new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    // Fill/skirt material: depth-TESTED so the ground layer conforms to terrain and is
    // occluded by foreground geometry (kills the floating-wall look). Walls keep _lineMaterial
    // (NoDepthTest) so threat markers stay x-ray-visible.
    private StandardMaterial3D _fillMaterial = new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private HashSet<Vector2I> _reachableSet = new();
    private Dictionary<Vector2I, int> _costMap = new();
    private Dictionary<Vector2I, int> _threatLevels;
    private bool _isPlayerZone = true;
    private float _animOffset = 0f;

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

    // Neighbor in direction HexDirs[d] shares edge EdgeForDir[d].
    // HexDirs runs CW, corners/edges run CCW → reflection (6 - d) % 6.
    private static readonly int[] EdgeForDir = { 0, 5, 4, 3, 2, 1 };


    public override void _Ready()
    {
        // _immediateMesh / _lineMaterial are field-initialized (see above) so
        // pre-tree calls from the skip-deploy handoff can't NRE. Any zone data
        // written before this point is already in the mesh — the MeshInstance
        // picks it up the moment it's created here.
        _borderMesh = new MeshInstance3D
        {
            Mesh = _immediateMesh,
            // No MaterialOverride: per-surface materials are set via SurfaceBegin(prim, mat)
            // so the fill (depth-tested) and walls (NoDepthTest) can differ.
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
        if (_reachableSet.Count == 0)
            return;
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
        foreach (var k in costMap.Keys)
            _reachableSet.Add(k);
        _isPlayerZone = true;
        _threatLevels = null;
        _lineMaterial.AlbedoColor = PlayerColor;
        _fillMaterial.AlbedoColor = PlayerColor;
        HideCostLabel();
        RebuildMesh();
    }

    /// <summary>Show the threat zone for an enemy unit (hover preview).</summary>
    public void ShowEnemyZone(HashSet<Vector2I> reachable, HexGridManager grid)
    {
        _grid = grid;
        _reachableSet = reachable;
        _threatLevels = null;
        _costMap.Clear();
        _isPlayerZone = false;
        _lineMaterial.AlbedoColor = EnemyColor;
        _fillMaterial.AlbedoColor = EnemyColor;
        HideCostLabel();
        RebuildMesh();
    }

    /// <summary>Tiered threat zone: tile -> attacks-landable level (0 = movement only).
    /// Fill darkens toward blood-red as the enemy can strike that tile more times.</summary>
    public void ShowEnemyZone(Dictionary<Vector2I, int> threatLevels, HexGridManager grid)
    {
        _grid = grid;
        _reachableSet = new HashSet<Vector2I>(threatLevels.Keys);
        _threatLevels = threatLevels;
        _costMap.Clear();
        _isPlayerZone = false;
        _lineMaterial.AlbedoColor = EnemyColor;
        _fillMaterial.AlbedoColor = EnemyColor;
        HideCostLabel();
        RebuildMesh();
    }

    /// <summary>Clear all zone display.</summary>
    public void Clear()
    {
        _reachableSet.Clear();
        _costMap.Clear();
        _threatLevels = null;
        _immediateMesh.ClearSurfaces();
        HideCostLabel();
    }

    /// <summary>
    /// Show or update the cost label for a hovered tile.
    /// Pass null tile to hide it.
    /// </summary>
    public void ShowCostLabelForTile(Vector2I axial, HexGridManager grid, int baseSpeed)
    {
        if (!_reachableSet.Contains(axial))
        { HideCostLabel(); return; }
        if (axial == _lastHoveredTile)
            return;
        _lastHoveredTile = axial;

        int stepCost = _costMap.TryGetValue(axial, out var c) ? c : -1;
        string label = stepCost < 0
            ? "1 AP"
            : $"1 AP  ({stepCost}/{baseSpeed} steps)";

        if (_costLabel == null)
            return;   // pre-_Ready call — label doesn't exist yet

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
        // Null-guard: _costLabel is built in _Ready; the handoff can call
        // through here before this node enters the tree.
        if (_costLabel != null)
            _costLabel.Visible = false;
        _lastHoveredTile = new Vector2I(int.MinValue, int.MinValue);
    }

    // ── Mesh building ─────────────────────────────────────────────────────

    private void RebuildMesh()
    {
        _immediateMesh.ClearSurfaces();
        if (_reachableSet.Count == 0)
            return;

        // ── Pass 1: tile fill ─────────────────────────────────────────────
        _immediateMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _fillMaterial);
        if (_isPlayerZone)
        {
            var fillColor = new Color(PlayerColor.R, PlayerColor.G, PlayerColor.B, 0.18f);
            foreach (var coord in _reachableSet)
                DrawFilledHex(coord, fillColor);
        }
        else if (_threatLevels != null && _threatLevels.Count > 0)
        {
            // Tiered blood-red fill by attacks-landable.
            foreach (var coord in _reachableSet)
                DrawFilledHex(coord, ThreatFillColor(
                    _threatLevels.TryGetValue(coord, out var lv) ? lv : 0));
        }
        else
        {
            var fillColor = new Color(EnemyColor.R, EnemyColor.G, EnemyColor.B, 0.15f);
            foreach (var coord in _reachableSet)
                DrawFilledHex(coord, fillColor);
        }
        _immediateMesh.SurfaceEnd();

        // ── Pass 2: border outline ────────────────────────────────────────
        // Tier-boundary walls: a wall rises at every step-up (and the outer edge),
        // drawn once from the higher-tier side so nested rings well up toward the enemy.
        // Player zone = one tier, so only the outer boundary gets a wall.
        _immediateMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _lineMaterial);
        foreach (var coord in _reachableSet)
        {
            int la = LevelAt(coord);
            for (int d = 0; d < 6; d++)
            {
                if (la > LevelAt(coord + HexDirs[d]))
                    DrawWall(coord, d, la);
            }
        }
        _immediateMesh.SurfaceEnd();
    }

    private static Color ThreatFillColor(int level)
    {
        if (level <= 0)
            return new Color(UITheme.ThreatMoveOnly.R, UITheme.ThreatMoveOnly.G, UITheme.ThreatMoveOnly.B, 0.10f);
        int maxT = Mathf.Max(1, UITheme.ThreatMaxTier);
        float t = Mathf.Clamp((level - 1f) / Mathf.Max(1, maxT - 1), 0f, 1f);
        Color c = UITheme.ThreatTierLow.Lerp(UITheme.ThreatTierHigh, t);
        float alpha = 0.22f + 0.11f * Mathf.Min(level, maxT);
        return new Color(c.R, c.G, c.B, alpha);
    }

    private void DrawFilledHex(Vector2I coord, Color color)
    {
        var tileData = _grid?.GetTile(coord);
        float thisTop = tileData != null ? tileData.Height * 0.5f : 0f;
        float tileY = thisTop + 0.02f; // lift the top face slightly

        var center2D = AxialToWorld2D(coord);
        var center3D = new Vector3(center2D.X, tileY, center2D.Y);

        // Top face
        for (int i = 0; i < 6; i++)
        {
            var cA = center2D + HexCorner(i);
            var cB = center2D + HexCorner((i + 1) % 6);

            var vA = new Vector3(cA.X, tileY, cA.Y);
            var vB = new Vector3(cB.X, tileY, cB.Y);

            _immediateMesh.SurfaceSetColor(color);
            _immediateMesh.SurfaceAddVertex(center3D);
            _immediateMesh.SurfaceSetColor(color);
            _immediateMesh.SurfaceAddVertex(vA);
            _immediateMesh.SurfaceSetColor(color);
            _immediateMesh.SurfaceAddVertex(vB);
        }

        // Skirts: wrap the fill down each exposed vertical face where this tile is
        // taller than its neighbor, so terrain height steps don't leave bare gaps.
        if (_grid == null)
            return;
        for (int d = 0; d < 6; d++)
        {
            var nb = _grid.GetTile(coord + HexDirs[d]);
            float nbTop = nb != null ? nb.Height * 0.5f : thisTop - SkirtVoidDepth;
            if (thisTop - nbTop <= 0.01f)
                continue;

            int edge = EdgeForDir[d];
            var off = ((HexCorner(edge) + HexCorner((edge + 1) % 6)) * 0.5f).Normalized() * SkirtOutwardOffset;
            var cA = center2D + HexCorner(edge) + off;
            var cB = center2D + HexCorner((edge + 1) % 6) + off;
            var tA = new Vector3(cA.X, tileY, cA.Y);
            var tB = new Vector3(cB.X, tileY, cB.Y);
            var bA = new Vector3(cA.X, nbTop, cA.Y);
            var bB = new Vector3(cB.X, nbTop, cB.Y);

            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(tA);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(tB);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(bB);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(tA);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(bB);
            _immediateMesh.SurfaceSetColor(color); _immediateMesh.SurfaceAddVertex(bA);
        }
    }

        /// <summary>Tier of a tile for wall stepping: threat level (0 = movement only),
    /// 0 for any player-zone tile, or -1 when the tile is outside the zone.</summary>
    private int LevelAt(Vector2I coord)
    {
        if (!_reachableSet.Contains(coord))
            return -1;
        if (_isPlayerZone || _threatLevels == null)
            return 0;
        return _threatLevels.TryGetValue(coord, out var lv) ? lv : 0;
    }

    private float TierHeight(int tier)
    {
        if (_isPlayerZone)
            return PlayerLipHeight;
        int maxT = Mathf.Max(1, UITheme.ThreatMaxTier);
        float f = Mathf.Clamp((float)tier / maxT, 0f, 1f);
        return WallHeight * Mathf.Lerp(0.4f, 1f, f);   // movement-only short, kill-zone tall
    }

    private Color WallColor(int tier)
    {
        if (_isPlayerZone)
            return PlayerColor;
        if (tier <= 0)
            return UITheme.ThreatMoveOnly;
        int maxT = Mathf.Max(1, UITheme.ThreatMaxTier);
        float t = Mathf.Clamp((tier - 1f) / Mathf.Max(1, maxT - 1), 0f, 1f);
        return UITheme.ThreatTierLow.Lerp(UITheme.ThreatTierHigh, t);
    }

    private float WallBaseAlpha(int tier)
    {
        if (_isPlayerZone)
            return 0.5f;
        int maxT = Mathf.Max(1, UITheme.ThreatMaxTier);
        float f = Mathf.Clamp((float)tier / maxT, 0f, 1f);
        return Mathf.Lerp(WallBaseAlphaMin, WallBaseAlphaMax, f);
    }

    /// <summary>Animated dashed vertical wall along the edge of `coord` facing
    /// `neighborDir`, rising to the tier's height and fading to transparent at the top.
    /// Reuses the border dash scroll so the wall reads as a live telegraph.</summary>
    private void DrawWall(Vector2I coord, int neighborDir, int tier)
    {
        float tileY = 0f;
        if (_grid != null)
        {
            var tileData = _grid.GetTile(coord);
            if (tileData != null)
                tileY = tileData.Height * 0.5f + 0.02f;
        }

        var center2D = AxialToWorld2D(coord);
        int edge = EdgeForDir[neighborDir];
        var cA = center2D + HexCorner(edge);
        var cB = center2D + HexCorner((edge + 1) % 6);

        float edgeLen = cA.DistanceTo(cB);
        var start3D = new Vector3(cA.X, tileY, cA.Y);
        var end3D = new Vector3(cB.X, tileY, cB.Y);
        var edgeVec = (end3D - start3D).Normalized();

        float dashWorldLen = DashLength * edgeLen;
        float cycleLen = edgeLen / Mathf.Max(1f, Mathf.Round(edgeLen / (dashWorldLen * 1.5f)));
        dashWorldLen = cycleLen * DashLength;

        float startOffset = (_animOffset * cycleLen * 2f) % cycleLen;
        float t = -startOffset;

        float height = TierHeight(tier);
        Color baseCol = WallColor(tier);
        baseCol.A = WallBaseAlpha(tier);
        Color topCol = baseCol;
        topCol.A = 0f;
        var up = new Vector3(0f, height, 0f);

        while (t < edgeLen)
        {
            float dashStart = Mathf.Max(t, 0f);
            float dashEnd = Mathf.Min(t + dashWorldLen, edgeLen);

            if (dashEnd > dashStart + 0.001f)
            {
                var b1 = start3D + edgeVec * dashStart;
                var b2 = start3D + edgeVec * dashEnd;
                var tt1 = b1 + up;
                var tt2 = b2 + up;

                // Two triangles (b1,b2,t2) + (b1,t2,t1); double-sided via CullMode.Disabled.
                _immediateMesh.SurfaceSetColor(baseCol); _immediateMesh.SurfaceAddVertex(b1);
                _immediateMesh.SurfaceSetColor(baseCol); _immediateMesh.SurfaceAddVertex(b2);
                _immediateMesh.SurfaceSetColor(topCol);  _immediateMesh.SurfaceAddVertex(tt2);

                _immediateMesh.SurfaceSetColor(baseCol); _immediateMesh.SurfaceAddVertex(b1);
                _immediateMesh.SurfaceSetColor(topCol);  _immediateMesh.SurfaceAddVertex(tt2);
                _immediateMesh.SurfaceSetColor(topCol);  _immediateMesh.SurfaceAddVertex(tt1);
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
