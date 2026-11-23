using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Reach.cs
//
// Purpose:        Combat overhaul stage C2. Three generation knobs a
//                 "reach" fight sets: a larger board, the enemy seeded
//                 somewhere other than its layout anchor, and a
//                 guarantee that the party can walk to the exit.
//                 All three are off by default, and off means the
//                 map generates exactly as it did before this file.
// Layer:          Combat / terrain generation
// Collaborators:  HexGridManager.cs (GenerateMap order),
//                 HexGridManager.Recipes.cs (ResolveRecipe, ResolveCoord),
//                 HexGridManager.Spawns.cs (GenerateSpawnPlan,
//                   EnsureConnectivity),
//                 CombatManager.cs (sets the knobs in
//                   ConfigureAndGenerateMap),
//                 CombatManager.Reach.cs (the objective)
// See:            docs/combat_c2_reach_and_field_board_v1.md
// ============================================================

public partial class HexGridManager
{
    /// <summary>Board growth in size steps. 0 = the recipe's own size. Each step adds
    /// one tile of radius to a hexagon or blob, two tiles of width and one and a half
    /// of height to a rectangle or blob. 2 turns the radius 5 hexagon (91 tiles) into
    /// radius 7 (169). Ignored on siege recipes and on recipes with no deployments
    /// block (the halls), whose layouts are authored to their exact size.</summary>
    public int BoardGrow = 0;

    /// <summary>A recipe coord token (midpoint, axis:N, flank:N...). When set, the
    /// enemy's one spawn zone is seeded there in place of whatever the deployment
    /// said. Empty = no override.</summary>
    public string EnemySpawnOverride = "";

    /// <summary>A recipe coord token the party must be able to walk to from its own
    /// spawn anchor. Checked after the spawn connectivity pass, and carved the same
    /// way when no path exists. Empty = no check.</summary>
    public string MustReachToken = "";

    private bool _enemySpawnOverridden;

    /// <summary>From ResolveRecipe, after the recipe's shape is applied.</summary>
    private void ApplyBoardGrow()
    {
        if (BoardGrow <= 0 || _activeRecipe == null)
            return;
        if (_activeRecipe.Siege != null
            || _activeRecipe.Deployments == null || _activeRecipe.Deployments.Count == 0)
        {
            GD.Print($"[Board] '{_activeRecipe.Id}' keeps its authored size (siege or hall).");
            return;
        }

        int beforeW = GridWidth;
        int beforeH = GridHeight;
        int beforeR = MapRadius;
        switch (Shape)
        {
            case MapShape.Hexagon:
            case MapShape.Triangle:
                MapRadius += BoardGrow;
                break;
            case MapShape.Blob:
                MapRadius += BoardGrow;
                GridWidth += BoardGrow * 2;
                GridHeight += Mathf.CeilToInt(BoardGrow * 1.5f);
                break;
            default:
                GridWidth += BoardGrow * 2;
                GridHeight += Mathf.CeilToInt(BoardGrow * 1.5f);
                break;
        }
        GD.Print($"[Board] '{_activeRecipe.Id}' grown by {BoardGrow}: " +
                 $"{beforeW}x{beforeH} r{beforeR} to {GridWidth}x{GridHeight} r{MapRadius}.");
    }

    /// <summary>From GenerateSpawnPlan, once both target lists are filled. Replaces the
    /// enemy targets with the one override coord. A plan that then fails to seat its
    /// headcount falls back to the layout anchors through the existing guarantee.</summary>
    private void ApplyEnemySpawnOverride()
    {
        _enemySpawnOverridden = false;
        if (string.IsNullOrEmpty(EnemySpawnOverride))
            return;
        if (_activeRecipe != null && _activeRecipe.Siege != null)
            return;

        Vector2I at = ResolveRecipeCoord(EnemySpawnOverride);
        _enemySpawnTargets.Clear();
        _enemySpawnTargets.Add(at);
        _enemySpawnOverridden = true;
        GD.Print($"[Deploy] enemy spawn overridden to '{EnemySpawnOverride}' at {at}.");
    }

    /// <summary>From GenerateMap, after EnsureConnectivityBetweenSpawns.</summary>
    private void EnsureReachConnectivity()
    {
        if (string.IsNullOrEmpty(MustReachToken))
            return;
        var playerZone = SpawnZones.Find(z => z.Side == SpawnSide.Player);
        if (playerZone == null)
            return;
        Vector2I goal = ResolveRecipeCoord(MustReachToken);
        if (!Tiles.ContainsKey(goal))
        {
            GD.PushWarning($"[Board] must-reach token '{MustReachToken}' resolved off the map at {goal}.");
            return;
        }
        EnsureConnectivity(playerZone.Anchor, goal);
    }
}
