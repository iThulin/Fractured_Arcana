using Godot;
using System.Collections.Generic;

/// <summary>
/// The player's party token on the overworld map.
/// Handles movement input, step spending, and movement animation.
/// </summary>
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
        // Draw the party token as a bright circle
        _tokenVisual = new Polygon2D
        {
            Polygon = MakeCirclePoints(14f, 12),
            Color = new Color(1f, 0.9f, 0.3f), // gold
            ZIndex = 10
        };
        AddChild(_tokenVisual);

        // Add a dark outline for visibility
        var outline = new Polygon2D
        {
            Polygon = MakeCirclePoints(17f, 12),
            Color = new Color(0.15f, 0.1f, 0f),
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
        if (_isMoving) return false;

        // Must be an adjacent hex
        var neighbors = _grid.GetNeighbors(CurrentCoord);
        if (!neighbors.Contains(targetCoord)) return false;

        // Can't walk into water (impassable)
        if (_grid.Hexes.TryGetValue(targetCoord, out var targetHex))
        {
            if (targetHex.Terrain == OverworldHex.TerrainType.Water)
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

        if (_grid == null) return;

        foreach (var neighborCoord in _grid.GetNeighbors(CurrentCoord))
        {
            if (_grid.Hexes.TryGetValue(neighborCoord, out var hex))
            {
                if (hex.Terrain == OverworldHex.TerrainType.Water) continue;

                // Add a subtle highlight overlay
                var highlight = new Polygon2D
                {
                    Polygon = OverworldHex.MakeHexPoints(OverworldHex.GetHexSize()),
                    Color = _highlightTint,
                    ZIndex = 4,
                    Name = "MoveHighlight"
                };
                hex.AddChild(highlight);
                _highlightedHexes.Add(hex);
            }
        }
    }

    private void ClearHighlights()
    {
        foreach (var hex in _highlightedHexes)
        {
            var highlight = hex.GetNodeOrNull("MoveHighlight");
            highlight?.QueueFree();
        }
        _highlightedHexes.Clear();
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