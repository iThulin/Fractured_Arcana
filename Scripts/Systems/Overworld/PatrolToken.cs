using Godot;
using System.Collections.Generic;

// ============================================================
// PatrolToken.cs
//
// Purpose:        A mobile faction token on the 2D overworld.
//                 Owned by OverworldFactionManager. On each
//                 world tick (one per player step), the patrol
//                 either hunts toward the player (if within
//                 DetectionRange hexes) or wanders seeded
//                 territory around its home coord. Fog-aware:
//                 visible only in Revealed hexes, ghosted in
//                 Silhouette hexes, hidden otherwise.
//
//                 Rendering: upward-pointing triangle (visually
//                 distinct from the party token's circle) in
//                 the archmage faction's color. Animates
//                 smoothly toward the logical position, matching
//                 the party token's movement pattern.
//
//                 One PatrolToken per patrol unit. Combat
//                 triggers when the token and party share a
//                 coord — handled by FactionManager, not here.
// Layer:          UI / System
// Collaborators:  OverworldFactionManager.cs (owner + ticker),
//                 OverworldHexGrid.cs (coord helpers),
//                 OverworldHex.cs (terrain + fog state)
// ============================================================

/// <summary>Mobile faction patrol on the overworld. Hunts the player when within detection range; otherwise wanders seeded territory. Fog-aware visibility.</summary>
public partial class PatrolToken : Node2D
{
    // ── Constants ─────────────────────────────────────────────────────────
    private const float BodyRadius = 11f;
    private const float OutlineRadius = 13.5f;
    private const float MoveSpeed = 220f; // pixels per second (slightly slower than party)
    private const int DetectionRange = 4; // hexes within which the patrol hunts
    private const int HomeRange = 4;      // max hexes the patrol wanders from its home

    // ── Public state ──────────────────────────────────────────────────────
    public Vector2I CurrentCoord { get; private set; }
    public string ArchmageId { get; private set; } = "";

    // ── Visual ───────────────────────────────────────────────────────────
    private Polygon2D _body;
    private Polygon2D _outline;
    private Label _indicator; // faction initial shown when visible in fog

    // ── Animation ────────────────────────────────────────────────────────
    private Vector2 _visualTarget;
    private bool _isAnimating;

    // ── Patrol logic ──────────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private Vector2I _homeCoord;
    private Vector2I _prevCoord;
    private RandomNumberGenerator _rng;
    private Color _factionColor;

    // ═══════════════════════════════════════════════════════════════════════
    // Setup
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Call once after adding this node to the scene. Must be called before
    /// the first Tick so the grid reference and RNG are ready.
    /// </summary>
    public void Initialize(
        OverworldHexGrid grid,
        Vector2I startCoord,
        Vector2I homeCoord,
        string factionColorHex,
        string archmageId,
        int seed)
    {
        _grid = grid;
        CurrentCoord = startCoord;
        _prevCoord = startCoord;
        _homeCoord = homeCoord;
        ArchmageId = archmageId;

        _factionColor = new Color(factionColorHex);

        _rng = new RandomNumberGenerator();
        _rng.Seed = (ulong)seed;

        Position = _grid.AxialToWorld(startCoord);
        _visualTarget = Position;

        BuildVisual(archmageId);
    }

    private void BuildVisual(string archmageId)
    {
        // Outline — slightly larger, dark for contrast
        _outline = new Polygon2D
        {
            Polygon = TrianglePoints(OutlineRadius),
            Color = new Color(0f, 0f, 0f, 0.7f),
            ZIndex = 6,
        };
        AddChild(_outline);

        // Filled body in faction color
        _body = new Polygon2D
        {
            Polygon = TrianglePoints(BodyRadius),
            Color = _factionColor,
            ZIndex = 7,
        };
        AddChild(_body);

        // Single-character initial so the player can identify the faction at a glance
        string initial = archmageId.Length > 0
            ? archmageId[..1].ToUpper()
            : "?";

        _indicator = new Label
        {
            Text = initial,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Position = new Vector2(-5f, -9f),
            ZIndex = 8,
        };
        _indicator.AddThemeFontSizeOverride("font_size", 9);
        _indicator.AddThemeColorOverride("font_color", Colors.White);
        AddChild(_indicator);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Per-frame
    // ═══════════════════════════════════════════════════════════════════════

    public override void _Process(double delta)
    {
        // Smooth visual movement toward logical position
        if (_isAnimating)
        {
            var diff = _visualTarget - Position;
            float dist = diff.Length();
            float step = MoveSpeed * (float)delta;

            if (step >= dist)
            {
                Position = _visualTarget;
                _isAnimating = false;
            }
            else
            {
                Position += diff.Normalized() * step;
            }
        }

        // Fog-aware visibility
        UpdateFogVisibility();
    }

    private void UpdateFogVisibility()
    {
        if (_grid == null || !_grid.Hexes.TryGetValue(CurrentCoord, out var hex))
        {
            Visible = false;
            return;
        }

        switch (hex.Fog)
        {
            case OverworldHex.FogState.Revealed:
                Visible = true;
                Modulate = Colors.White;
                break;
            case OverworldHex.FogState.Silhouette:
                // Ghosted — player knows something is there
                Visible = true;
                Modulate = new Color(1f, 1f, 1f, 0.28f);
                break;
            case OverworldHex.FogState.Hidden:
                Visible = false;
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // World tick
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by OverworldFactionManager on every player step. Moves one hex.
    /// </summary>
    public void Tick(Vector2I playerCoord)
    {
        if (_grid == null)
            return;

        int distToPlayer = _grid.Distance(CurrentCoord, playerCoord);

        Vector2I next;
        if (distToPlayer <= DetectionRange)
        {
            // Hunting — step toward player as directly as possible
            next = StepToward(CurrentCoord, playerCoord);
        }
        else if (_grid.Distance(CurrentCoord, _homeCoord) > HomeRange)
        {
            // Drifted too far from home territory — return
            next = StepToward(CurrentCoord, _homeCoord);
        }
        else
        {
            // Wander seeded territory around home
            next = Wander();
        }

        if (next != CurrentCoord)
            MoveTo(next);
    }

    /// <summary>Force the token to a specific coord (used when restoring from combat).</summary>
    public void TeleportTo(Vector2I coord)
    {
        CurrentCoord = coord;
        _prevCoord = coord;
        Position = _grid.AxialToWorld(coord);
        _visualTarget = Position;
        _isAnimating = false;
    }

    public bool IsOnSameHex(Vector2I coord) => CurrentCoord == coord;

    // ═══════════════════════════════════════════════════════════════════════
    // Movement helpers
    // ═══════════════════════════════════════════════════════════════════════

    private void MoveTo(Vector2I coord)
    {
        _prevCoord = CurrentCoord;
        CurrentCoord = coord;
        _visualTarget = _grid.AxialToWorld(coord);
        _isAnimating = true;
    }

    /// <summary>
    /// Returns the passable neighbor of <c>from</c> that minimises hex
    /// distance to <c>target</c>. Returns <c>from</c> if no passable
    /// neighbor is closer (the patrol is already adjacent or blocked).
    /// </summary>
    private Vector2I StepToward(Vector2I from, Vector2I target)
    {
        var neighbors = _grid.GetNeighbors(from);
        Vector2I best = from;
        int bestDist = _grid.Distance(from, target);

        foreach (var n in neighbors)
        {
            if (!IsPassable(n))
                continue;
            int d = _grid.Distance(n, target);
            if (d < bestDist)
            { bestDist = d; best = n; }
        }

        return best;
    }

    /// <summary>
    /// Picks a random passable neighbor, preferring not to immediately
    /// backtrack to the previous position.
    /// </summary>
    private Vector2I Wander()
    {
        var neighbors = _grid.GetNeighbors(CurrentCoord);
        var candidates = new List<Vector2I>();

        foreach (var n in neighbors)
        {
            if (!IsPassable(n))
                continue;
            if (n == _prevCoord)
                continue; // avoid immediate backtrack
            candidates.Add(n);
        }

        // If nothing available except the previous hex, allow backtracking
        if (candidates.Count == 0)
        {
            foreach (var n in neighbors)
                if (IsPassable(n))
                    candidates.Add(n);
        }

        if (candidates.Count == 0)
            return CurrentCoord;
        return candidates[(int)(_rng.Randi() % (uint)candidates.Count)];
    }

    private bool IsPassable(Vector2I coord)
    {
        if (!_grid.Hexes.TryGetValue(coord, out var hex))
            return false;
        return hex.Terrain != OverworldHex.TerrainType.Water &&
               hex.Terrain != OverworldHex.TerrainType.Mountain;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Visual helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Upward-pointing equilateral triangle centered at origin.</summary>
    private static Vector2[] TrianglePoints(float radius) => new Vector2[]
    {
        new Vector2(0f, -radius),
        new Vector2( radius * 0.866f,  radius * 0.5f),
        new Vector2(-radius * 0.866f,  radius * 0.5f),
    };
}
