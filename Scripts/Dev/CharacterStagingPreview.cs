using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CharacterStagingPreview.cs
//
// Purpose:        Dev-only staging harness. Drives the REAL
//                 HexGridManager generation pipeline (same mesh
//                 builder, splat shader, theme atmosphere) at a
//                 small blob radius, then drops one Unit on the
//                 centre tile so you can see a character lit and
//                 grounded exactly as it will be in combat —
//                 without booting a full encounter (no cards, no
//                 AI, no deck, no CombatManager).
//
//                 All live tweaking is driven by the sibling
//                 StagingControlPanel, which calls the public
//                 methods on this node.
// Layer:          Dev tooling (NOT shipped in combat)
// Collaborators:  HexGridManager.cs (generation — called directly,
//                 NOT via the deferred auto-bootstrap),
//                 Unit.cs (SetBodyColor / PlaceOnTile),
//                 SchoolColors.cs (school tint source),
//                 CameraController.cs (sibling, framing),
//                 Combat_Environment.tres (via WorldEnvironment).
//
// Divergence note (read before trusting the lighting):
//   Battlefield.tscn does NOT currently wire the grid's
//   ThemeSun / ThemeWorldEnvironment exports, so
//   ApplyThemeAtmosphere() is dormant in live combat right now.
//   This preview DOES wire them, so theme lighting here is a
//   PREVIEW of what combat will look like once those exports are
//   wired in Battlefield.tscn — not a 1:1 of current combat.
// ============================================================

/// <summary>
/// Standalone character-in-environment previewer. Runs the production grid
/// generator at a small radius and stages a single <see cref="Unit"/> on it.
/// Construction order is explicit: the grid is generated synchronously by THIS
/// node (front-running the grid's own deferred auto-bootstrap), so tiles exist
/// before the unit is placed.
/// </summary>
public partial class CharacterStagingPreview : Node3D
{
    [ExportGroup("Wiring")]
    /// <summary>The real grid. Leave its own exports configured in the .tscn; this node only sets size/seed/theme then calls GenerateMap().</summary>
    [Export] public HexGridManager Grid;

    /// <summary>The unit scene to stage. Defaults to the player Unit.tscn.</summary>
    [Export] public PackedScene UnitScene;

    /// <summary>Sibling camera controller (named "CameraController" so the grid's CenterCameraOverGrid finds it too).</summary>
    [Export] public CameraController Camera;

    [ExportGroup("Initial Staging")]
    /// <summary>Blob radius for the preview board. 2 ≈ 19 tiles — enough terrain context without a full arena.</summary>
    [Export(PropertyHint.Range, "1,4,1")] public int PreviewRadius = 2;

    /// <summary>Starting theme. The panel can change this at runtime.</summary>
    [Export] public HexGridManager.MapTheme StartTheme = HexGridManager.MapTheme.ArcaneMeadow;

    /// <summary>School used for the initial body tint. The panel can change this at runtime.</summary>
    [Export] public CardSchool StartSchool = CardSchool.Elementalist;

    // ── Runtime state ───────────────────────────────────────────────────────
    private Unit _stagedUnit;
    private CardSchool _currentSchool;
    private int _currentSeed;

    /// <summary>Fires after every (re)generation so the panel can resync its seed field.</summary>
    public event Action<int> OnRegenerated;

    public override void _Ready()
    {
        // Per the Godot 4.6 cross-platform rule: don't build/instantiate scene
        // children directly in _Ready(). Defer the whole staging sequence.
        CallDeferred(nameof(BuildStage));
    }

    /// <summary>Full staging sequence: configure grid → generate → place unit → frame camera.</summary>
    private void BuildStage()
    {
        if (!ResolveWiring())
            return;

        _currentSchool = StartSchool;
        Grid.Theme = StartTheme;

        // Small blob, deterministic-on-demand. Seed 0 = grid randomises and writes
        // the chosen seed back into Grid.MapSeed, which we then read for the panel.

        Regenerate(randomizeSeed: true);
    }

    private bool ResolveWiring()
    {
        if (Grid == null)
            Grid = GetNodeOrNull<HexGridManager>("HexGridManager");
        if (Camera == null)
            Camera = GetNodeOrNull<CameraController>("CameraController");
        if (UnitScene == null)
            UnitScene = GD.Load<PackedScene>("res://Scenes/Combat/Players/Unit.tscn");

        if (Grid == null)
        {
            GD.PrintErr("[StagingPreview] No HexGridManager wired or found as child 'HexGridManager'.");
            return false;
        }
        if (UnitScene == null)
        {
            GD.PrintErr("[StagingPreview] No UnitScene wired and Unit.tscn not found at default path.");
            return false;
        }
        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Public API — the control panel calls these.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regenerates the board using the production pipeline. When randomizeSeed is
    /// true a fresh seed is rolled; otherwise the current locked seed is reused so
    /// the layout is stable across theme changes. The staged unit is re-placed on
    /// the new centre tile afterwards.
    /// </summary>
    public void Regenerate(bool randomizeSeed)
    {
        if (Grid == null)
            return;

        // Clear any previously generated tiles/props/obstacles so GenerateMap
        // starts clean. GenerateMap itself clears Tiles, but the child HexTile
        // nodes from a prior run are still parented to the grid — free them.
        ClearGridChildren();

        Grid.MapSeed = randomizeSeed ? 0 : _currentSeed;

        // Direct, synchronous generation. This front-runs the grid's own
        // AutoGenerateIfEmpty (which only fires if Tiles is still empty), so by
        // the time that deferred call lands, Tiles.Count > 0 and it no-ops.
        Grid.GenerateMap();

        // GenerateMap writes the chosen seed back when it started at 0.
        _currentSeed = Grid.MapSeed;

        StageUnit();

        OnRegenerated?.Invoke(_currentSeed);
    }

    /// <summary>Sets the theme and regenerates. Keeps the current seed so only the theme accents/atmosphere change, not the underlying layout.</summary>
    public void SetTheme(HexGridManager.MapTheme theme)
    {
        if (Grid == null)
            return;
        Grid.Theme = theme;
        Regenerate(randomizeSeed: false);
    }

    /// <summary>Locks generation to a specific seed and regenerates with it.</summary>
    public void SetSeed(int seed)
    {
        _currentSeed = seed;
        Regenerate(randomizeSeed: false);
    }

    public int CurrentSeed => _currentSeed;

    /// <summary>Rolls a new random seed and regenerates.</summary>
    public void RandomizeSeed() => Regenerate(randomizeSeed: true);

    /// <summary>Applies a school's border colour to the staged unit's body via the real SetBodyColor path.</summary>
    public void SetSchoolTint(CardSchool school)
    {
        _currentSchool = school;
        ApplySchoolTint();
    }

    /// <summary>Applies an arbitrary body colour directly (raw colour-picker path), bypassing the school mapping.</summary>
    public void SetBodyColor(Color color)
    {
        _stagedUnit?.SetBodyColor(color);
    }

    public Unit StagedUnit => _stagedUnit;

    // ════════════════════════════════════════════════════════════════════════
    // Internals
    // ════════════════════════════════════════════════════════════════════════

    private void StageUnit()
    {
        // Reuse the existing unit across regenerations; only instantiate once.
        if (_stagedUnit == null || !IsInstanceValid(_stagedUnit))
        {
            _stagedUnit = UnitScene.Instantiate<Unit>();
            _stagedUnit.IsPlayerControlled = true;
            _stagedUnit.TeamId = 0;
            _stagedUnit.DisplayName = "Preview";
            // _Ready() fires on AddChild and builds rings / reads HealthBarRoot —
            // all self-contained, safe outside combat.
            AddChild(_stagedUnit);
            _stagedUnit.School = _currentSchool;
        }

        var centreTile = FindCentreTile();
        if (centreTile == null)
        {
            GD.PrintErr("[StagingPreview] No walkable centre tile found to stage the unit.");
            return;
        }

        // Detach from any prior tile (regeneration replaced the TileData objects).
        _stagedUnit.PlaceOnTile(centreTile);
        _stagedUnit.RefreshNameLabel();

        ApplySchoolTint();

        // Frame the staged unit specifically. The grid already framed the whole
        // board in GenerateMap; FocusOn tightens onto the character.
        Camera?.FocusOn(_stagedUnit);
    }

    /// <summary>
    /// Picks a walkable tile nearest the axial origin to stand the unit on. The
    /// blob is centred on (0,0), but the exact centre tile may be water/blocked,
    /// so we scan outward for the closest walkable, unoccupied tile.
    /// </summary>
    private TileData FindCentreTile()
    {
        TileData best = null;
        int bestDist = int.MaxValue;

        foreach (var kvp in Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null || tile.TileView == null)
                continue;
            if (!tile.IsWalkable || tile.IsBlocked || tile.IsOccupied)
                continue;

            // Hex distance from origin (cube metric, matches grid convention).
            var c = kvp.Key;
            int dist = (Math.Abs(c.X) + Math.Abs(-c.X - c.Y) + Math.Abs(c.Y)) / 2;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = tile;
            }
        }

        return best;
    }

    private void ApplySchoolTint()
    {
        if (_stagedUnit == null)
            return;
        _stagedUnit.School = _currentSchool;
        // Real tint path: SetBodyColor replaces surface 0's material with a flat
        // albedo material. NOTE: this discards the mesh's original material on
        // surface 0 and only affects surface 0 — a multi-surface mesh (e.g. a
        // separate hat surface) won't tint. That's a property of Unit.SetBodyColor,
        // reflected faithfully here.
        _stagedUnit.SetBodyColor(SchoolColors.GetBorderColor(_currentSchool));
    }

    private void ClearGridChildren()
    {
        // Free generated HexTile nodes and any grouped props/obstacles so a
        // regeneration doesn't stack duplicate geometry under the grid.
        foreach (Node child in Grid.GetChildren())
        {
            if (child is HexTile)
                child.QueueFree();
            else if (child.IsInGroup("generated_prop") || child.IsInGroup("generated_obstacle"))
                child.QueueFree();
        }
    }
}
