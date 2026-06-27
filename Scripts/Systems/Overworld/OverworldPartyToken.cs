using Godot;
using System.Collections.Generic;

// ============================================================
// OverworldPartyToken.cs
//
// Purpose:        Player party token on the 2D overworld.
//                 Handles click-to-move input, animates movement
//                 between hex centres, fires Moved/Arrived
//                 signals, and renders the move-highlight overlay.
// Layer:          UI
// Collaborators:  OverworldHexGrid.cs (host grid),
//                 FogOfWarManager.cs (visibility updates on move),
//                 OverworldRunManager.cs (consumes signals,
//                 spends steps)
// See:            (none)
// ============================================================

/// <summary>The player's 2D party token. Owns its current coord, handles tween-animated moves between hex centres, and emits signals on move start and arrival.</summary>
public partial class OverworldPartyToken : Node2D
{
    public Vector2I CurrentCoord { get; private set; }

    // Visual
    private Polygon2D _tokenVisual;
    private float _moveSpeed = 300f; // pixels per second
    private bool _isMoving = false;
    private Vector2 _moveTarget;

    // References (set by RunManager during setup)
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;

    // Movement highlight
    private List<OverworldHex> _highlightedHexes = new();
    private Color _highlightTint = new Color(1f, 1f, 0.6f, 0.3f);

    [Signal] public delegate void PartyMovedEventHandler(Vector2I newCoord, Vector2I oldCoord);
    [Signal] public delegate void PartyArrivedEventHandler(Vector2I coord);

    public override void _Ready()
    {
        _tokenVisual = new Polygon2D
        {
            Polygon = MakeCirclePoints(UITheme.PartyTokenRadius, UITheme.PartyTokenSegments),
            Color = UITheme.PartyTokenFill,
            ZIndex = 10
        };
        AddChild(_tokenVisual);

        var outline = new Polygon2D
        {
            Polygon = MakeCirclePoints(UITheme.PartyTokenOutlineRadius, UITheme.PartyTokenSegments),
            Color = UITheme.PartyTokenOutline,
            ZIndex = 9
        };
        AddChild(outline);
    }

    /// <summary>
    /// Call once during run setup to place the token on the entry hex.
    /// </summary>
    public void Initialize(OverworldHexGrid grid, FogOfWarManager fog, Vector2I startCoord)
    {
        _grid = grid;
        _fog = fog;
        CurrentCoord = startCoord;
        Position = _grid.AxialToWorld(startCoord);

        // Initial fog reveal
        _fog.RevealLandmarks();
        _fog.UpdateVision(CurrentCoord);

        // Show where the player can move
        HighlightMoveOptions();
    }

    public override void _Process(double delta)
    {
        if (_isMoving)
        {
            // Smooth movement toward target hex
            var direction = (_moveTarget - Position).Normalized();
            float distance = Position.DistanceTo(_moveTarget);
            float step = _moveSpeed * (float)delta;

            if (step >= distance)
            {
                // Arrived
                Position = _moveTarget;
                _isMoving = false;
                EmitSignal(SignalName.PartyArrived, CurrentCoord);
                HighlightMoveOptions();
            }
            else
            {
                Position += direction * step;
            }
        }
    }

    /// <summary>
    /// Attempt to move the party to the target hex.
    /// Returns true if movement was valid and initiated.
    /// </summary>
    public bool TryMoveTo(Vector2I targetCoord)
    {
        if (_isMoving)
            return false;

        // Must be an adjacent hex
        var neighbors = _grid.GetNeighbors(CurrentCoord);
        if (!neighbors.Contains(targetCoord))
            return false;

        // Can't walk into water (impassable)
        if (_grid.Hexes.TryGetValue(targetCoord, out var targetHex))
        {
            if (targetHex.IsWater)
                return false;
        }

        // Move
        var oldCoord = CurrentCoord;
        CurrentCoord = targetCoord;
        _moveTarget = _grid.AxialToWorld(targetCoord);
        _isMoving = true;

        // Clear old highlights while moving
        ClearHighlights();

        // Update fog immediately (feels better than waiting for arrival)
        _fog.UpdateVision(CurrentCoord);

        EmitSignal(SignalName.PartyMoved, CurrentCoord, oldCoord);
        return true;
    }

    /// <summary>
    /// Highlight adjacent hexes the party can move to.
    /// Gives the player clear feedback about their options.
    /// </summary>
    private void HighlightMoveOptions()
    {
        ClearHighlights();

        if (_grid == null)
            return;

        foreach (var neighborCoord in _grid.GetNeighbors(CurrentCoord))
        {
            if (_grid.Hexes.TryGetValue(neighborCoord, out var hex))
            {
                if (hex.IsWater)
                    continue;

                // Color the highlight based on cost
                int cost = GetTerrainCostPreview(hex.Terrain);
                Color tint = cost switch
                {
                    1 => UITheme.MoveHighlightCheap,
                    2 => UITheme.MoveHighlightModerate,
                    3 => UITheme.MoveHighlightExpensive,
                    _ => UITheme.MoveHighlightDefault,
                };

                var highlight = new Polygon2D
                {
                    Polygon = OverworldHex.MakeHexPoints(OverworldHex.GetHexSize()),
                    Color = tint,
                    ZIndex = 4,
                    Name = "MoveHighlight"
                };
                hex.AddChild(highlight);

                // Cost label on the hex
                var costLabel = new Label
                {
                    Text = cost.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Position = new Vector2(-6, 8),
                    ZIndex = 5,
                    Name = "CostLabel"
                };
                costLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldCostLabelFontSize);
                costLabel.AddThemeColorOverride("font_color", Colors.White);
                hex.AddChild(costLabel);

                _highlightedHexes.Add(hex);
            }
        }
    }

    private void ClearHighlights()
    {
        foreach (var hex in _highlightedHexes)
        {
            hex.GetNodeOrNull("MoveHighlight")?.QueueFree();
            hex.GetNodeOrNull("CostLabel")?.QueueFree();
        }
        _highlightedHexes.Clear();
    }

    private int GetTerrainCostPreview(OverworldHex.TerrainType terrain)
    {
        return terrain switch
        {
            OverworldHex.TerrainType.Road => 1,
            OverworldHex.TerrainType.Grassland => 1,
            OverworldHex.TerrainType.ArcaneGround => 1,
            OverworldHex.TerrainType.Forest => 2,
            OverworldHex.TerrainType.Ruins => 2,
            OverworldHex.TerrainType.Swamp => 2,
            OverworldHex.TerrainType.Mountain => 3,
            OverworldHex.TerrainType.Volcanic => 2,
            _ => 1
        };
    }

    private Vector2[] MakeCirclePoints(float radius, int segments)
    {
        var pts = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float angle = Mathf.Tau * i / segments;
            pts[i] = new Vector2(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle));
        }
        return pts;
    }
}