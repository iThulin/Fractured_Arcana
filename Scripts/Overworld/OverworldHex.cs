using Godot;
using System;

/// <summary>
/// A single hex tile on the overworld exploration map.
/// Renders as a flat-top hexagon polygon. Handles its own click input.
/// </summary>
public partial class OverworldHex : Node2D
{
    // ── Data ────────────────────────────────────────────────────────────
    public Vector2I Axial { get; set; }

    public enum TerrainType { Grassland, Forest, Road, Ruins, Mountain, Swamp, ArcaneGround, Volcanic, Water }
    public TerrainType Terrain { get; set; } = TerrainType.Grassland;

    public enum FogState { Hidden, Silhouette, Revealed }
    public FogState Fog { get; set; } = FogState.Hidden;

    public enum POIType { None, Combat, Rest, Objective, Narrative }
    public POIType POI { get; set; } = POIType.None;
    public bool POIConsumed { get; set; } = false;

    // ── Visuals ─────────────────────────────────────────────────────────
    private Polygon2D _polygon;
    private Polygon2D _fogOverlay;
    private Polygon2D _poiMarker;
    private Label _debugLabel;

    private static readonly float HEX_SIZE = 36f; // pixel radius of each hex

    // ── Signals ─────────────────────────────────────────────────────────
    [Signal] public delegate void HexClickedEventHandler(Vector2I axial);

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
            Width = 1.5f,
            DefaultColor = new Color(0.2f, 0.2f, 0.25f, 0.6f),
            ZIndex = 1
        };
        AddChild(border);

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
            TerrainType.Grassland    => new Color(0.42f, 0.72f, 0.32f),  // vivid green
            TerrainType.Forest       => new Color(0.15f, 0.50f, 0.18f),  // deep forest green
            TerrainType.Road         => new Color(0.82f, 0.75f, 0.58f),  // warm tan
            TerrainType.Ruins        => new Color(0.58f, 0.50f, 0.42f),  // warm brown-grey
            TerrainType.Mountain     => new Color(0.65f, 0.63f, 0.60f),  // light stone grey
            TerrainType.Swamp        => new Color(0.38f, 0.50f, 0.28f),  // olive murk
            TerrainType.ArcaneGround => new Color(0.55f, 0.38f, 0.78f),  // rich purple
            TerrainType.Volcanic     => new Color(0.75f, 0.30f, 0.12f),  // deep orange-red
            TerrainType.Water        => new Color(0.22f, 0.50f, 0.82f),  // bright blue
            _ => Colors.Gray
        };

        // Fog overlay — less oppressive, more readable
        _fogOverlay.Color = Fog switch
        {
            FogState.Hidden     => new Color(0.12f, 0.12f, 0.18f, 0.88f), // dark but not black
            FogState.Silhouette => new Color(0.12f, 0.12f, 0.18f, 0.45f), // clearly see terrain color
            FogState.Revealed   => new Color(0, 0, 0, 0),                  // fully clear
            _ => Colors.Black
        };

        // POI marker — larger for visibility
        bool showPOI = POI != POIType.None && !POIConsumed && Fog == FogState.Revealed;
        _poiMarker.Visible = showPOI;
        if (showPOI)
        {
            _poiMarker.Color = POI switch
            {
                POIType.Combat    => new Color(0.95f, 0.20f, 0.20f),  // bright red
                POIType.Rest      => new Color(0.20f, 0.80f, 0.95f),  // cyan
                POIType.Objective => new Color(1.0f, 0.90f, 0.15f),   // bright gold
                POIType.Narrative => new Color(0.75f, 0.50f, 1.0f),   // purple
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
}