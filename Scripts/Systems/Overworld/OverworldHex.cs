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

    public enum TerrainType { Grassland, Forest, Road, Ruins, Mountain, Swamp, ArcaneGround, Volcanic, Water }
    public TerrainType Terrain { get; set; } = TerrainType.Grassland;

    public enum FogState { Hidden, Silhouette, Revealed }
    public FogState Fog { get; set; } = FogState.Hidden;

    public enum POIType { None, Combat, Rest, Objective, Narrative, Negotiation, Outpost }
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
            Width = UITheme.HexBorderWidth,
            DefaultColor = UITheme.HexBorderColor,
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
            TerrainType.Grassland => UITheme.TerrainGrassland,
            TerrainType.Forest => UITheme.TerrainForest,
            TerrainType.Road => UITheme.TerrainRoad,
            TerrainType.Ruins => UITheme.TerrainRuins,
            TerrainType.Mountain => UITheme.TerrainMountain,
            TerrainType.Swamp => UITheme.TerrainSwamp,
            TerrainType.ArcaneGround => UITheme.TerrainArcaneGround,
            TerrainType.Volcanic => UITheme.TerrainVolcanic,
            TerrainType.Water => UITheme.TerrainWater,
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