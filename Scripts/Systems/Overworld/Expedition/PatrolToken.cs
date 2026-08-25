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
//                 coord. That is handled by FactionManager, not here.
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
    private const int DetectionRange = 8;     // hexes within which the patrol spots you
    private const int HomeRange = 4;          // legacy wander radius (pre-detection only)
    private const int LoseInterestRange = 14; // once committed, only gives up beyond this

    // ── Public state ──────────────────────────────────────────────────────
    public Vector2I CurrentCoord { get; private set; }
    public string ArchmageId { get; private set; } = "";

    // ── Visual ───────────────────────────────────────────────────────────
    private Polygon2D _body;
    private Polygon2D _outline;
    private Label _indicator; // faction initial shown when visible in fog
    private Line2D _vectorLine; // S3 Foreboding: pursuit-direction arrow

    // ── Animation ────────────────────────────────────────────────────────
    private Vector2 _visualTarget;
    private bool _isAnimating;

    // ── Step 4 (convergence spec): injected queries ──────────────────────
    // Passability and fog-visibility read DATA through these, wired by
    // OverworldFactionManager from ExpeditionManager's seams. Null (isolation)
    // falls back to the old node reads.

    /// <summary>World tile at a grid-local coord, or null off-world.</summary>
    public System.Func<Vector2I, WorldTile?> TileQuery;

    /// <summary>Fog at a grid-local coord (the ExpeditionFogModel).</summary>
    public System.Func<Vector2I, OverworldHex.FogState> FogQuery;

    // ── Patrol logic ──────────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private Vector2I _homeCoord;
    private Vector2I _prevCoord;
    private bool _committed = false;          // true once it has spotted the player
    private RandomNumberGenerator _rng;
    private Color _factionColor;

    // ── S3: spell-imposed stun (Stasis Snare / Fulminant Charge) ──────────
    // Distinct from disengagement: a stunned patrol HOLDS POSITION (it does
    // not rout home) and resumes exactly where it froze. Delay, not removal (G3).
    private int _stunSteps;

    /// <summary>True while frozen by a spell/trap: no move, no hunt, no capture.</summary>
    public bool IsStunned => _stunSteps > 0;

    /// <summary>Freeze in place for N party steps (does not stack; keeps the longer).</summary>
    public void Stun(int steps) => _stunSteps = System.Math.Max(_stunSteps, steps);

    // ── Disengagement after capture ─────────────────────────────────────────

    private int _recoveryCooldown; // steps during which the patrol stays home and won't hunt/capture

    /// <summary>True while routed and recovering, so it will not hunt or capture.</summary>
    public bool IsDisengaged => _recoveryCooldown > 0;

    /// <summary>Remaining recovery steps. Saved/restored across combat scene swaps.</summary>
    public int RecoveryCooldown => _recoveryCooldown;

    /// <summary>Restore a remaining cooldown without moving the token (position is restored separately).</summary>
    public void SetRecoveryCooldown(int steps) => _recoveryCooldown = Mathf.Max(0, steps);

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
        // Outline: slightly larger, dark for contrast
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

        // S3 Foreboding: pursuit vector, hidden until the attunement + a
        // committed hunt make it meaningful.
        _vectorLine = new Line2D
        {
            Points = new[] { Vector2.Zero, new Vector2(0, -18f) },
            Width = 3f,
            DefaultColor = new Color(1f, 0.85f, 0.3f, 0.9f),
            ZIndex = 8,
            Visible = false,
        };
        AddChild(_vectorLine);
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

        // S3 (True Names, Enchanter attunement): the token names itself in
        // full at sight instead of a bare initial.
        if (_indicator != null)
        {
            string full = ArchmageId.Length > 0 ? ArchmageId : "?";
            string wanted = OverworldSpellEffects.TrueNamesVision
                ? char.ToUpper(full[0]) + full.Substring(1)
                : full[..1].ToUpper();
            if (_indicator.Text != wanted)
            {
                _indicator.Text = wanted;
                _indicator.Position = OverworldSpellEffects.TrueNamesVision
                    ? new Vector2(-5f - 3.5f * (wanted.Length - 1), -9f)
                    : new Vector2(-5f, -9f);
            }
        }

        // S3 (Foreboding, Chronomancer attunement): a committed hunter shows
        // its pursuit vector, the direction of its current advance.
        if (_vectorLine != null)
            _vectorLine.Visible = OverworldSpellEffects.ForebodingVision && _committed;

        // Step 4: fog from the model when wired; node mirror in isolation.
        var fog = FogQuery != null ? FogQuery(CurrentCoord) : hex.Fog;
        switch (fog)
        {
            case OverworldHex.FogState.Revealed:
                Visible = true;
                Modulate = Colors.White;
                break;
            case OverworldHex.FogState.Silhouette:
                // Ghosted, so the player knows something is there
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

    public void Tick(Vector2I playerCoord)
    {
        if (_grid == null)
            return;

        // S3: stunned, so hold position, no hunt, no capture. Ticks down per
        // party step and resumes exactly where it froze.
        if (_stunSteps > 0)
        {
            _stunSteps--;
            return;
        }

        // Routed and recovering: hold at home, ignore the player.
        if (_recoveryCooldown > 0)
        {
            _recoveryCooldown--;
            Vector2I home = _grid.Distance(CurrentCoord, _homeCoord) > 0
                ? StepToward(CurrentCoord, _homeCoord)
                : Wander();
            if (home != CurrentCoord)
                MoveTo(home);
            return;
        }

        int distToPlayer = _grid.Distance(CurrentCoord, playerCoord);

        // S3 (Veil, Enchanter): the party is imperceptible, so detection fails
        // and a committed hunter loses the trail. The patrol keeps its route;
        // interception simply fails (G3). Checked here so the pursuit logic
        // below sees a world without the party in it.
        if (OverworldSpellEffects.VeilActive())
            _committed = false;
        // Spot the player within detection range → commit to the hunt.
        else if (distToPlayer <= DetectionRange)
            _committed = true;
        // Lose the trail only if the player breaks well clear.
        else if (_committed && distToPlayer > LoseInterestRange)
            _committed = false;

        Vector2I next;
        if (_committed)
        {
            // Relentless pursuit, with no home leash. Equal speed means it shadows a
            // moving player and closes on a dithering one.
            next = StepToward(CurrentCoord, playerCoord);
        }
        else if (_grid.Distance(CurrentCoord, _homeCoord) > HomeRange)
        {
            next = StepToward(CurrentCoord, _homeCoord);    // drift back to territory
        }
        else
        {
            next = Wander();
        }

        if (next != CurrentCoord)
            MoveTo(next);
    }

    /// <summary>
    /// Routs the patrol after a fight: teleports it home (its archmage's
    /// territory) and suppresses hunting/capture for <paramref name="cooldownSteps"/>
    /// player steps. After that it resumes patrolling automatically.
    /// </summary>
    public void Disengage(int cooldownSteps)
    {
        _recoveryCooldown = cooldownSteps;
        _committed = false;
        TeleportTo(_homeCoord); // ← swap _homeCoord for an archmage-seat coord if you add one
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

        // S3 Foreboding: point the pursuit vector along this advance.
        if (_vectorLine != null)
        {
            var dir = (_visualTarget - _grid.AxialToWorld(_prevCoord)).Normalized();
            if (dir != Vector2.Zero)
                _vectorLine.Points = new[] { Vector2.Zero, dir * 20f };
        }
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
        // Step 4: the loaded gate is EXPLICIT. This is the simulation LOD
        // (patrols freeze when their ground unloads), no longer an accident
        // of node existence at the read site.
        if (!_grid.Hexes.ContainsKey(coord))
            return false;
        // S3 (Thornwall, Druid): spell-denied hexes are impassable to
        // patrols only, since the party walks them freely.
        if (OverworldSpellEffects.HexBlockedForPatrols(coord))
            return false;
        // Terrain from the WORLD when wired; node fallback in isolation.
        if (TileQuery != null)
        {
            var t = TileQuery(coord);
            return t.HasValue && !t.Value.IsWater &&
                   t.Value.Terrain != OverworldHex.TerrainType.Mountain;
        }
        return _grid.Hexes.TryGetValue(coord, out var hex) &&
               hex.IsWater == false &&
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
