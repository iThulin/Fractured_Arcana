using Godot;
using System.Collections.Generic;

// ============================================================
// RoamerToken.cs
//
// Purpose:        A NON-HOSTILE roaming agent (merchant caravan)
//                 on the expedition map — the living-map minimum
//                 (discovery_loop_spec Layer E). Wanders one hex
//                 per player step; never hunts, never captures.
//                 When it and the party share a hex, the
//                 ExpeditionManager offers a one-time opportunity
//                 encounter, so the map generates a moment you
//                 didn't author tile-by-tile. Fog-aware like a
//                 patrol, so you glimpse it crossing the distance.
// Layer:          UI / System
// Collaborators:  ExpeditionManager.cs (spawns, ticks, contact),
//                 OverworldHexGrid.cs (coord helpers),
//                 OverworldHex.cs (terrain + fog state)
// Cf.:            PatrolToken.cs (the hostile sibling)
// ============================================================

/// <summary>Non-hostile roaming caravan. Wanders each player step; contact is
/// resolved by ExpeditionManager, not here. Distinct amber diamond visual.</summary>
public partial class RoamerToken : Node2D
{
    private const float Radius = 10f;
    private const float OutlineRadius = 12.5f;
    private const float MoveSpeed = 180f;

    public Vector2I CurrentCoord { get; private set; }

    private OverworldHexGrid _grid;
    private Vector2I _prevCoord;
    private RandomNumberGenerator _rng;

    private Vector2 _visualTarget;
    private bool _isAnimating;

    public void Initialize(OverworldHexGrid grid, Vector2I start, int seed)
    {
        _grid = grid;
        CurrentCoord = start;
        _prevCoord = start;
        _rng = new RandomNumberGenerator { Seed = (ulong)seed };
        Position = _grid.AxialToWorld(start);
        _visualTarget = Position;
        BuildVisual();
    }

    private void BuildVisual()
    {
        AddChild(new Polygon2D
        {
            Polygon = DiamondPoints(OutlineRadius),
            Color = new Color(0f, 0f, 0f, 0.7f),
            ZIndex = 6,
        });
        AddChild(new Polygon2D
        {
            Polygon = DiamondPoints(Radius),
            Color = new Color(0.92f, 0.72f, 0.30f), // caravan amber
            ZIndex = 7,
        });
        AddChild(new Polygon2D
        {
            Polygon = DiamondPoints(Radius * 0.34f),
            Color = new Color(0.16f, 0.12f, 0.08f),
            ZIndex = 8,
        });
    }

    public override void _Process(double delta)
    {
        if (_isAnimating)
        {
            var diff = _visualTarget - Position;
            float dist = diff.Length();
            float step = MoveSpeed * (float)delta;
            if (step >= dist) { Position = _visualTarget; _isAnimating = false; }
            else Position += diff.Normalized() * step;
        }
        UpdateFogVisibility();
    }

    private void UpdateFogVisibility()
    {
        if (_grid == null || !_grid.Hexes.TryGetValue(CurrentCoord, out var hex))
        { Visible = false; return; }
        switch (hex.Fog)
        {
            case OverworldHex.FogState.Revealed:
                Visible = true; Modulate = Colors.White; break;
            case OverworldHex.FogState.Silhouette:
                Visible = true; Modulate = new Color(1f, 1f, 1f, 0.30f); break;
            default:
                Visible = false; break;
        }
    }

    /// <summary>Wander one hex per player step — non-hostile, never hunts.</summary>
    public void Tick()
    {
        if (_grid == null) return;
        var next = Wander();
        if (next != CurrentCoord) MoveTo(next);
    }

    public bool IsOnSameHex(Vector2I coord) => CurrentCoord == coord;

    public void TeleportTo(Vector2I coord)
    {
        CurrentCoord = coord;
        _prevCoord = coord;
        if (_grid != null) { Position = _grid.AxialToWorld(coord); _visualTarget = Position; }
        _isAnimating = false;
    }

    private void MoveTo(Vector2I coord)
    {
        _prevCoord = CurrentCoord;
        CurrentCoord = coord;
        _visualTarget = _grid.AxialToWorld(coord);
        _isAnimating = true;
    }

    private Vector2I Wander()
    {
        var neighbors = _grid.GetNeighbors(CurrentCoord);
        var candidates = new List<Vector2I>();
        foreach (var n in neighbors)
        {
            if (!IsPassable(n) || n == _prevCoord) continue;
            candidates.Add(n);
        }
        if (candidates.Count == 0)
            foreach (var n in neighbors)
                if (IsPassable(n)) candidates.Add(n);
        if (candidates.Count == 0) return CurrentCoord;
        return candidates[(int)(_rng.Randi() % (uint)candidates.Count)];
    }

    private bool IsPassable(Vector2I coord)
        => _grid.Hexes.TryGetValue(coord, out var hex)
           && !hex.IsWater && hex.Terrain != OverworldHex.TerrainType.Mountain;

    private static Vector2[] DiamondPoints(float r) => new Vector2[]
    {
        new Vector2(0f, -r), new Vector2(r, 0f), new Vector2(0f, r), new Vector2(-r, 0f),
    };
}
