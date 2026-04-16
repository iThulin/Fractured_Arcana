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

    public enum POIType { None, Combat, Rest, Objective }
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
        // Build the hex polygon (flat-top orientation)
        var points = MakeHexPoints(HEX_SIZE);

        // Base terrain polygon
        _polygon = new Polygon2D { Polygon = points };
        AddChild(_polygon);

        // Fog overlay (drawn on top, semi-transparent or opaque)
        _fogOverlay = new Polygon2D { Polygon = points, ZIndex = 1 };
        AddChild(_fogOverlay);

        // POI marker (small diamond in the center)
        _poiMarker = new Polygon2D
        {
            Polygon = MakeHexPoints(HEX_SIZE * 0.25f),
            ZIndex = 2,
            Visible = false
        };
        AddChild(_poiMarker);

        // Clickable area
        var area = new Area2D();
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
            ZIndex = 3,
            Visible = false // toggle for debug
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
        // Terrain color (programmer art — replace later)
        _polygon.Color = Terrain switch
        {
            TerrainType.Grassland    => new Color(0.45f, 0.68f, 0.35f),
            TerrainType.Forest       => new Color(0.18f, 0.42f, 0.15f),
            TerrainType.Road         => new Color(0.72f, 0.65f, 0.50f),
            TerrainType.Ruins        => new Color(0.50f, 0.45f, 0.40f),
            TerrainType.Mountain     => new Color(0.55f, 0.55f, 0.55f),
            TerrainType.Swamp        => new Color(0.35f, 0.45f, 0.30f),
            TerrainType.ArcaneGround => new Color(0.40f, 0.30f, 0.65f),
            TerrainType.Volcanic     => new Color(0.60f, 0.25f, 0.15f),
            TerrainType.Water        => new Color(0.20f, 0.45f, 0.72f),
            _ => Colors.Gray
        };

        // Fog overlay
        _fogOverlay.Color = Fog switch
        {
            FogState.Hidden     => new Color(0.08f, 0.08f, 0.12f, 0.92f), // near-opaque dark
            FogState.Silhouette => new Color(0.08f, 0.08f, 0.12f, 0.55f), // see terrain shape, muted
            FogState.Revealed   => new Color(0, 0, 0, 0),                  // fully clear
            _ => Colors.Black
        };

        // POI marker
        bool showPOI = POI != POIType.None && !POIConsumed && Fog == FogState.Revealed;
        _poiMarker.Visible = showPOI;
        if (showPOI)
        {
            _poiMarker.Color = POI switch
            {
                POIType.Combat    => new Color(0.85f, 0.25f, 0.25f), // red
                POIType.Rest      => new Color(0.25f, 0.75f, 0.85f), // blue
                POIType.Objective => new Color(1.0f, 0.85f, 0.15f),  // gold
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