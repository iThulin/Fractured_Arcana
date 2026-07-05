using Godot;
using System;

// ============================================================
// OverworldHex.cs
//
// Purpose:        One tile on the 2D overworld exploration map.
//                 Renders the hex polygon + fog overlay + POI
//                 marker + debug label, and forwards click input
//                 to its grid via a Godot signal.
// Layer:          UI
// Collaborators:  OverworldHexGrid.cs (parent grid),
//                 FogOfWarManager.cs (sets Fog),
//                 POIGenerator.cs (sets POI), UITheme.cs (colours)
// See:            README §3 — overworld layer
// ============================================================

/// <summary>One 2D hex on the overworld exploration map. Holds its axial coord, terrain type, fog state, and POI assignment, and renders itself as a flat-top hex polygon with optional fog overlay and POI marker child.</summary>
public partial class OverworldHex : Node2D
{
    // ── Data ────────────────────────────────────────────────────────────
    public Vector2I Axial { get; set; }

    public enum TerrainType { Grassland, Forest, Road, Ruins, Mountain, Swamp, ArcaneGround, Volcanic, Water, Hills, Coast, Lake, Desert, Tundra, Snow, Marsh }
    public bool IsWater => TerrainClass.IsWater(Terrain);
    public bool IsLand => TerrainClass.IsLand(Terrain);
    public bool IsCoast => TerrainClass.IsCoast(Terrain);
    public TerrainType Terrain { get; set; } = TerrainType.Grassland;

    public enum FogState { Hidden, Silhouette, Revealed }
    public FogState Fog { get; set; } = FogState.Hidden;

    public enum POIType { None, Combat, Rest, Objective, Narrative, Negotiation, Outpost }
    public POIType POI { get; set; } = POIType.None;
    public bool POIConsumed { get; set; } = false;

    /// <summary>River/road edge masks copied from the world tile (6-bit, same
    /// convention as WorldTile). Drawn as lines along the hex edges in _Ready.</summary>
    public byte RiverEdges { get; set; } = 0;
    public byte RoadEdges { get; set; } = 0;
    public byte SpringEdges { get; set; } = 0;
    public byte OceanDepth { get; set; } = 0;

    // ── Visuals ─────────────────────────────────────────────────────────
    private Polygon2D _polygon;
    private Polygon2D _fogOverlay;
    private Polygon2D _poiMarker;
    private Label _debugLabel;

    private static readonly float HEX_SIZE = 36f; // pixel radius of each hex

    // ── Signals ─────────────────────────────────────────────────────────
    [Signal] public delegate void HexClickedEventHandler(Vector2I axial);
    [Signal] public delegate void HexHoveredEventHandler(Vector2I axial);
    [Signal] public delegate void HexUnhoveredEventHandler(Vector2I axial);

    public override void _Ready()
    {
        var points = MakeHexPoints(HEX_SIZE);

        // Base terrain polygon
        _polygon = new Polygon2D { Polygon = points };
        AddChild(_polygon);

        // Hex border outline — makes tiles visually distinct
        var borderPoints = new Vector2[7];
        for (int i = 0; i < 6; i++)
            borderPoints[i] = points[i];
        borderPoints[6] = points[0]; // close the loop

        var border = new Line2D
        {
            Points = borderPoints,
            Width = UITheme.HexBorderWidth,
            DefaultColor = UITheme.HexBorderColor,
            ZIndex = 1
        };
        AddChild(border);
        BuildEdgeLines(points);

        // Fog overlay (drawn on top)
        _fogOverlay = new Polygon2D { Polygon = points, ZIndex = 2 };
        AddChild(_fogOverlay);

        // POI marker (bigger for visibility)
        _poiMarker = new Polygon2D
        {
            Polygon = MakeHexPoints(HEX_SIZE * 0.3f),  // slightly larger than before
            ZIndex = 3,
            Visible = false
        };
        AddChild(_poiMarker);

        // Clickable area
        var area = new Area2D { ZIndex = 5 };
        var collider = new CollisionPolygon2D { Polygon = points };
        area.AddChild(collider);
        area.InputEvent += OnAreaInput;
        area.MouseEntered += () => EmitSignal(SignalName.HexHovered, Axial);
        area.MouseExited += () => EmitSignal(SignalName.HexUnhovered, Axial);
        AddChild(area);

        // Debug coordinate label
        _debugLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Position = new Vector2(-20, -10),
            ZIndex = 4,
            Visible = false
        };
        AddChild(_debugLabel);
        _debugLabel.Text = $"{Axial.X},{Axial.Y}";

        RefreshVisuals();
    }

    /// <summary>
    /// Call this after changing Terrain, Fog, or POI to update the hex's appearance.
    /// </summary>
    public void RefreshVisuals()
    {
        // Brighter, more saturated terrain palette with clear visual identity
        _polygon.Color = Terrain switch
        {
            TerrainType.Grassland => UITheme.TerrainGrassland,
            TerrainType.Forest => UITheme.TerrainForest,
            TerrainType.Road => UITheme.TerrainRoad,
            TerrainType.Ruins => UITheme.TerrainRuins,
            TerrainType.Mountain => UITheme.TerrainMountain,
            TerrainType.Swamp => UITheme.TerrainSwamp,
            TerrainType.ArcaneGround => UITheme.TerrainArcaneGround,
            TerrainType.Volcanic => UITheme.TerrainVolcanic,
            TerrainType.Water => UITheme.OceanColor(OceanDepth),
            TerrainType.Hills => UITheme.TerrainHills,
            TerrainType.Coast => UITheme.TerrainCoast,
            TerrainType.Lake => UITheme.TerrainLake,
            TerrainType.Desert => UITheme.TerrainDesert,
            TerrainType.Tundra => UITheme.TerrainTundra,
            TerrainType.Snow => UITheme.TerrainSnow,
            TerrainType.Marsh => UITheme.TerrainMarsh,
            _ => Colors.Gray
        };

        // Fog overlay — less oppressive, more readable
        _fogOverlay.Color = Fog switch
        {
            FogState.Hidden => UITheme.FogHidden,
            FogState.Silhouette => UITheme.FogSilhouette,
            FogState.Revealed => UITheme.FogRevealed,
            _ => Colors.Black
        };

        // POI marker — larger for visibility
        bool showPOI = POI != POIType.None && !POIConsumed && Fog == FogState.Revealed;
        _poiMarker.Visible = showPOI;
        if (showPOI)
        {
            _poiMarker.Color = POI switch
            {
                POIType.Combat => UITheme.POICombat,
                POIType.Rest => UITheme.POIRest,
                POIType.Objective => UITheme.POIObjective,
                POIType.Narrative => UITheme.POINarrative,
                POIType.Negotiation => UITheme.POINegotiation,
                POIType.Outpost => UITheme.POIOutpost,
                _ => Colors.White
            };
        }
    }

    private void OnAreaInput(Node viewport, InputEvent @event, long shapeIdx)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            EmitSignal(SignalName.HexClicked, Axial);
        }
        else if (@event is InputEventMouseMotion)
        {
            EmitSignal(SignalName.HexHovered, Axial);
        }
    }

    /// <summary>
    /// Generates vertices for a flat-top regular hexagon.
    /// </summary>
    public static Vector2[] MakeHexPoints(float size)
    {
        var pts = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            float angleDeg = 60 * i;
            float angleRad = Mathf.DegToRad(angleDeg);
            pts[i] = new Vector2(size * Mathf.Cos(angleRad), size * Mathf.Sin(angleRad));
        }
        return pts;
    }

    public static float GetHexSize() => HEX_SIZE;

    // Direction index -> the two hex-vertex indices of that edge (flat-top; matches
    // HexCoord.AxialDirections cross-referenced with AxialToWorld neighbour offsets).
    private static readonly int[,] EdgeVerts =
        { {0,1}, {5,0}, {4,5}, {3,4}, {2,3}, {1,2} };

    /// <summary>Draws river/road/spring edges as Line2D segments along the hex's
    /// edges. Both adjacent hexes draw the shared edge (they coincide in world space),
    /// so a window-fringe tile still shows its own edges without its neighbour loaded.
    /// Road over river is drawn last, reading as a bridge deck; springs are thin.</summary>
    /// <summary>Draws river/road/spring networks as smooth noisy curves that cut
    /// THROUGH the hex interior, rather than straight segments along the rim. Each lit
    /// edge contributes its midpoint; midpoints are connected via quadratic Béziers
    /// bowed through the hex centre, so a channel passing through reads as one flowing
    /// line. Because edge-midpoints are shared between adjacent hexes (same two
    /// vertices), neighbouring hexes' curves meet exactly — the network is continuous
    /// without either hex knowing the other. Road-over-river draws last (bridge deck).</summary>
    private void BuildEdgeLines(Vector2[] pts)
    {
        if (RiverEdges == 0 && RoadEdges == 0 && SpringEdges == 0)
            return;

        float riverW = HEX_SIZE * 0.15f;
        float roadW = HEX_SIZE * 0.10f;
        float springW = HEX_SIZE * 0.08f;

        // Springs first (under), then rivers, then roads (bridge deck on top).
        // Springs only where there's no river on that edge (matches prior behaviour).
        BuildChannel(pts, (byte)(SpringEdges & ~RiverEdges), springW, UITheme.TerrainLake, wander: 0.55f);
        BuildChannel(pts, RiverEdges, riverW, UITheme.TerrainWater, wander: 1.0f);
        BuildChannel(pts, RoadEdges, roadW, UITheme.TerrainRoad, wander: 0.35f);
    }

    /// <summary>Build one channel's curves from its edge mask. Pairs of lit edges become
    /// a through-curve (midpoint→centre→midpoint); a lone lit edge becomes a stub
    /// (midpoint→centre); 3+ edges fan each midpoint to the centre (a confluence).</summary>
    private void BuildChannel(Vector2[] pts, byte mask, float width, Color color, float wander)
    {
        if (mask == 0)
            return;

        // Collect midpoints of all lit edges, with their direction for noise seeding.
        var mids = new System.Collections.Generic.List<(Vector2 p, int dir)>();
        for (int d = 0; d < 6; d++)
        {
            if ((mask & (1 << d)) == 0)
                continue;
            Vector2 mid = (pts[EdgeVerts[d, 0]] + pts[EdgeVerts[d, 1]]) * 0.5f;
            mids.Add((mid, d));
        }

        Vector2 center = Vector2.Zero; // hex centre in local space (polygon is centred on the node)

        if (mids.Count == 1)
        {
            // A lone lit edge — either a true source/mouth, or a one-tile gap in the
            // flow data mid-river. Extend the curve THROUGH the centre to the opposite
            // edge's midpoint, so a single missing-edge dropout still reads as a
            // continuous channel rather than a dead-end stub. The opposite edge is
            // (dir + 3) % 6 — the antipodal hex side.
            int dir = mids[0].dir;
            int opp = (dir + 3) % 6;
            Vector2 oppMid = (pts[EdgeVerts[opp, 0]] + pts[EdgeVerts[opp, 1]]) * 0.5f;
            AddCurve(mids[0].p, center, oppMid, width, color, wander, dir, opp);
        }
        else if (mids.Count == 2)
        {
            // The common case — a channel passing straight through. One curve, bowed
            // through (near) the centre so it cuts across the tile.
            AddCurve(mids[0].p, center, mids[1].p, width, color, wander, mids[0].dir, mids[1].dir);
        }
        else
        {
            // Confluence: each edge feeds the centre. Curves overlap at the centre and
            // read as a junction.
            foreach (var m in mids)
                AddCurve(m.p, center, center, width, color, wander, m.dir, m.dir);
        }
    }

    /// <summary>Sample a quadratic Bézier (a→control→b) into a Line2D, nudging the
    /// control point and interior samples by deterministic per-hex noise so the path
    /// wanders organically. When b == control (a stub), degenerates to a curved spoke
    /// from a to the centre.</summary>
    private void AddCurve(Vector2 a, Vector2 control, Vector2 b, float width, Color color,
                          float wander, int seedA, int seedB)
    {
        const int Samples = 10;

        // Perturb the control point off the centre, so the curve doesn't always pass
        // dead through the middle. Direction + amount seeded per-hex + per-edge so it's
        // stable across rebuilds but varies tile to tile.
        float n = HexNoise(seedA * 7 + seedB * 13);
        Vector2 perp = (b - a).Orthogonal().Normalized();
        if (!perp.IsFinite())
            perp = Vector2.Right;
        Vector2 ctrl = control + perp * (HEX_SIZE * 0.18f * wander * n);

        var points = new Vector2[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            float t = (float)i / Samples;
            // Quadratic Bézier.
            Vector2 p = a.Lerp(ctrl, t).Lerp(ctrl.Lerp(b, t), t);
            // Light per-sample jitter for an organic edge (zero at the endpoints so
            // shared midpoints stay exact — continuity with neighbours must not break).
            float endFade = Mathf.Sin(t * Mathf.Pi); // 0 at ends, 1 at middle
            float j = (HexNoise(seedA * 31 + i * 17) - 0.5f) * HEX_SIZE * 0.10f * wander * endFade;
            Vector2 jdir = (b - a).Orthogonal().Normalized();
            if (!jdir.IsFinite())
                jdir = Vector2.Up;
            points[i] = p + jdir * j;
        }

        var line = new Line2D
        {
            Points = points,
            Width = width,
            DefaultColor = color,
            ZIndex = 1,                       // above terrain/border, below fog(2) + POI(3)
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
            JointMode = Line2D.LineJointMode.Round,
        };
        AddChild(line);
    }

    /// <summary>Deterministic [0,1] hash noise keyed on this hex's axial coord plus a
    /// salt, so curve wander is stable across rebuilds (combat round-trips rebuild the
    /// window) yet differs per tile and per edge.</summary>
    private float HexNoise(int salt)
    {
        int h = Axial.X * 374761393 + Axial.Y * 668265263 + salt * 2147483647;
        h = (h ^ (h >> 13)) * 1274126177;
        h &= 0x7fffffff;
        return (h % 10000) / 10000f;
    }

    private void AddEdgeLine(Vector2 a, Vector2 b, float width, Color color)
    {
        var line = new Line2D
        {
            Points = new[] { a, b },
            Width = width,
            DefaultColor = color,
            ZIndex = 1,                       // above terrain/border, below fog(2) + POI(3)
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
        };
        AddChild(line);
    }
}